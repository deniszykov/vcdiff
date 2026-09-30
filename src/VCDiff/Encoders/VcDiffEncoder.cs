// Copyright (c) Denis Zykov and contributors (DeepSeek, ClaudeCode).
// Licensed under the Apache License, Version 2.0.

using System;
using System.Buffers;
using Microsoft.IO;
using VCDiff.Shared;

namespace VCDiff.Encoders;

/// <summary>
///     A stateful, span-based streaming VCDIFF encoder that follows the zlib-style
///     <see cref="OperationStatus" /> contract.
/// </summary>
/// <remarks>
///     <para>
///         The encoder is a block algorithm: the VCDIFF format is window based, so the encoder
///         accumulates target bytes until a full window is available, then encodes and emits that
///         window. <see cref="OperationStatus.Done" /> is returned at each window boundary, and again
///         once the final window has been flushed after <c>isFinal</c> is set.
///     </para>
///     <para>
///         The dictionary is not copied: its segments are pinned and read in place, so it may be made of
///         many small buffers. The memory behind it must stay alive and unchanged until
///         <see cref="Dispose" /> is called.
///     </para>
/// </remarks>
public sealed class VcDiffEncoder : IDisposable
{
	private static readonly byte[] MagicBytes = { 0xD6, 0xC3, 0xC4, 0x00, 0x00 };
	private static readonly byte[] MagicBytesExtended = { 0xD6, 0xC3, 0xC4, (byte)'S', 0x00 };
	private readonly ChunkEncoder _chunker;

	private readonly DictionarySource _dictionary;
	private readonly RollingHash _hasher;
	private readonly bool _ownsHasher;

	private readonly RecyclableMemoryStream _pending;
	private readonly ArrayPool<byte> _pool;

	private readonly byte[] _targetWindow;
	private readonly int _windowSize;
	private bool _disposed;

	private bool _finished;
	private long _pendingReadOffset;
	private int _targetLength;

    /// <summary>
    ///     Creates a streaming VCDIFF encoder.
    /// </summary>
    /// <param name="dictionary">
    ///     The dictionary (source/base) data. It is referenced, not copied, and must outlive this
    ///     instance.
    /// </param>
    /// <param name="options">The encoder options. See <see cref="VcEncoderOptions" />.</param>
    public VcDiffEncoder(ReadOnlySequence<byte> dictionary, VcEncoderOptions? options = null)
	{
		options ??= new VcEncoderOptions();
		this._pool = options.BytePoolOrDefault;

		var interleaved = options.Interleaved;
		var checksumFormat = options.ChecksumFormat;
		if (interleaved && checksumFormat == ChecksumFormat.Xdelta3)
			throw new ArgumentException("Interleaved diffs can not have an xdelta3 checksum!");

		var maxWindowSize = options.MaxBufferSize;
		if (maxWindowSize <= 0)
			maxWindowSize = 1;
		this._windowSize = maxWindowSize * 1024 * 1024;

		var blockSize = options.BlockSize;
		var chunkSize = options.ChunkSize < 2 ? blockSize * 2 : options.ChunkSize;
		if (blockSize % 2 != 0 || chunkSize < 2 || chunkSize < 2 * blockSize)
			throw new ArgumentException($"{blockSize} can not be less than 2 or twice the blocksize of the dictionary {blockSize}.");

		var rollingHash = options.RollingHash;
		if (rollingHash == null)
		{
			this._hasher = new RollingHash(blockSize);
			this._ownsHasher = true;
		}
		else
		{
			this._hasher = rollingHash;
			this._ownsHasher = false;
		}

		if (this._hasher.WindowSize != blockSize)
			throw new ArgumentException("Supplied RollingHash instance has a different window size than blocksize!");

		this._dictionary = new DictionarySource(dictionary);
		var blockHash = new BlockHash(this._dictionary, this._hasher, blockSize);
		blockHash.AddAllBlocks();

		this._chunker = new ChunkEncoder(blockHash, this._dictionary.Length, this._hasher, checksumFormat, interleaved, chunkSize);

		this._targetWindow = this._pool.Rent(this._windowSize);
		this._pending = Pool.MemoryStreamManager.GetStream(nameof(VcDiffEncoder));

		var magic = !interleaved && checksumFormat != ChecksumFormat.SDCH ? MagicBytes : MagicBytesExtended;
		this._pending.Write(magic.AsSpan());
	}

    /// <summary>
    ///     Encodes as much target data as possible, writing delta bytes into <paramref name="output" />.
    /// </summary>
    /// <param name="input">The target data to encode.</param>
    /// <param name="output">The destination for delta bytes.</param>
    /// <param name="inputConsumed">The number of <paramref name="input" /> bytes consumed.</param>
    /// <param name="outputWritten">The number of bytes written to <paramref name="output" />.</param>
    /// <param name="isFinal">Whether this is the final chunk of target data.</param>
    /// <returns>The transformation status.</returns>
    public OperationStatus Encode(ReadOnlySpan<byte> input, Span<byte> output, out int inputConsumed, out int outputWritten, bool isFinal)
	{
		if (this._disposed)
			throw new ObjectDisposedException(nameof(VcDiffEncoder));

		inputConsumed = 0;
		outputWritten = 0;

		if (this._finished)
			return OperationStatus.Done;

		// Drain any pending encoded output first.
		outputWritten = this.DrainPending(output);
		if (this._pendingReadOffset < this._pending.Length)
			return OperationStatus.DestinationTooSmall;

		// Accumulate target input into the current window.
		while (inputConsumed < input.Length)
		{
			var space = this._windowSize - this._targetLength;
			var take = Math.Min(input.Length - inputConsumed, space);
			input.Slice(inputConsumed, take).CopyTo(this._targetWindow.AsSpan(this._targetLength, take));
			this._targetLength += take;
			inputConsumed += take;

			if (this._targetLength == this._windowSize)
			{
				this.EncodeWindow();
				outputWritten += this.DrainPending(output.Slice(outputWritten));
				if (this._pendingReadOffset < this._pending.Length)
					return OperationStatus.DestinationTooSmall;

				return OperationStatus.Done;
			}
		}

		if (isFinal)
		{
			if (this._targetLength > 0)
			{
				this.EncodeWindow();
				outputWritten += this.DrainPending(output.Slice(outputWritten));
				if (this._pendingReadOffset < this._pending.Length)
					return OperationStatus.DestinationTooSmall;
			}

			this._finished = true;
			return OperationStatus.Done;
		}

		return OperationStatus.NeedMoreData;
	}

	private void EncodeWindow()
	{
		using var target = new ByteBuffer(new Memory<byte>(this._targetWindow, 0, this._targetLength));
		this._pending.Position = this._pending.Length;
		this._chunker.EncodeChunk(target, this._pending);
		this._targetLength = 0;
	}

	private int DrainPending(Span<byte> output)
	{
		var available = this._pending.Length - this._pendingReadOffset;
		if (available <= 0)
			return 0;

		var toCopy = (int)Math.Min(available, output.Length);
		if (toCopy > 0)
		{
			var sequence = this._pending.GetReadOnlySequence().Slice(this._pendingReadOffset, toCopy);
			var offset = 0;
			foreach (var segment in sequence)
			{
				segment.Span.CopyTo(output.Slice(offset));
				offset += segment.Span.Length;
			}
		}

		this._pendingReadOffset += toCopy;
		if (this._pendingReadOffset == this._pending.Length)
		{
			this._pending.SetLength(0);
			this._pending.Position = 0;
			this._pendingReadOffset = 0;
		}

		return toCopy;
	}

	/// <inheritdoc />
	public void Dispose()
	{
		if (this._disposed)
			return;

		this._disposed = true;
		this._chunker.Dispose();
		this._dictionary.Dispose();
		if (this._ownsHasher) this._hasher.Dispose();
		this._pending.Dispose();
		this._pool.Return(this._targetWindow, false);
	}
}