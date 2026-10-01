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
	private readonly ChunkEncoder _chunker;
	private readonly RecyclableMemoryStream _pending;
	private readonly ArrayPool<byte> _pool;
	private readonly EncoderSession _session;

	private readonly int _windowSize;
	private bool _disposed;

	private bool _finished;
	private long _pendingReadOffset;
	private int _targetLength;

	// Rented on first use: callers that hand over whole windows at once never need it.
	private byte[]? _targetWindow;

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
		EncoderSession.ValidateFormat(interleaved, checksumFormat);

		this._session = new EncoderSession(new DictionarySource(dictionary), options);
		try
		{
			this._windowSize = this._session.WindowSize;
			this._chunker = this._session.CreateChunkEncoder(interleaved, checksumFormat);
			this._pending = options.MemoryStreamManagerOrDefault.GetStream(nameof(VcDiffEncoder));
			this._pending.Write(EncoderSession.GetFileHeader(interleaved, checksumFormat).Span);
		}
		catch
		{
			this._chunker?.Dispose();
			this._session.Dispose();
			throw;
		}
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
			var available = input.Length - inputConsumed;

			// A whole window (or the final, shorter one) that is already contiguous in the caller's
			// span is encoded in place, without staging it in the window buffer.
			if (this._targetLength == 0 && (available >= this._windowSize || isFinal))
			{
				var length = Math.Min(available, this._windowSize);
				this.EncodeWindow(input.Slice(inputConsumed, length));
				inputConsumed += length;

				outputWritten += this.DrainPending(output.Slice(outputWritten));
				if (this._pendingReadOffset < this._pending.Length)
					return OperationStatus.DestinationTooSmall;

				if (length == this._windowSize)
					return OperationStatus.Done;

				this._finished = true;
				return OperationStatus.Done;
			}

			this._targetWindow ??= this._pool.Rent(this._windowSize);
			var space = this._windowSize - this._targetLength;
			var take = Math.Min(available, space);
			input.Slice(inputConsumed, take).CopyTo(this._targetWindow.AsSpan(this._targetLength, take));
			this._targetLength += take;
			inputConsumed += take;

			if (this._targetLength == this._windowSize)
			{
				this.EncodeBufferedWindow();
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
				this.EncodeBufferedWindow();
				outputWritten += this.DrainPending(output.Slice(outputWritten));
				if (this._pendingReadOffset < this._pending.Length)
					return OperationStatus.DestinationTooSmall;
			}

			this._finished = true;
			return OperationStatus.Done;
		}

		return OperationStatus.NeedMoreData;
	}

	private void EncodeBufferedWindow()
	{
		this.EncodeWindow(this._targetWindow.AsSpan(0, this._targetLength));
		this._targetLength = 0;
	}

	private void EncodeWindow(ReadOnlySpan<byte> window)
	{
		this._pending.Position = this._pending.Length;
		this._chunker.EncodeChunk(window, this._pending);
	}

	private int DrainPending(Span<byte> output)
	{
		var available = this._pending.Length - this._pendingReadOffset;
		if (available <= 0)
			return 0;

		// Read through the stream: it locates the block by offset, so draining into tiny output spans
		// stays linear and does not rebuild a segment list on every call.
		var toCopy = (int)Math.Min(available, output.Length);
		var copied = 0;
		this._pending.Position = this._pendingReadOffset;
		while (copied < toCopy)
		{
			var read = this._pending.Read(output.Slice(copied, toCopy - copied));
			if (read <= 0)
				break;

			copied += read;
		}

		this._pendingReadOffset += copied;
		if (this._pendingReadOffset == this._pending.Length)
		{
			this._pending.SetLength(0);
			this._pending.Position = 0;
			this._pendingReadOffset = 0;
		}

		return copied;
	}

	/// <inheritdoc />
	public void Dispose()
	{
		if (this._disposed)
			return;

		this._disposed = true;
		this._chunker.Dispose();
		this._session.Dispose();
		this._pending.Dispose();
		if (this._targetWindow != null)
		{
			this._pool.Return(this._targetWindow, false);
			this._targetWindow = null;
		}
	}
}