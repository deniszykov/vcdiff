// Copyright (c) Metric and the Snowflake Authors.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Buffers;
using System.IO;
using System.Threading.Tasks;
using Microsoft.IO;
using VCDiff.Includes;
using VCDiff.Shared;

namespace VCDiff.Encoders;

/// <summary>
///     A simple VCDIFF Encoder class.
/// </summary>
public class VcEncoder : IDisposable
{
	private readonly ArrayPool<byte> _bytePool;
	private readonly RecyclableMemoryStreamManager _manager;
	private readonly Stream _outputStream;
	private readonly EncoderSession _session;
	private readonly Stream _targetData;

	// Holds the dictionary read from a source stream, in pooled blocks rather than one large array.
	// Stays null when the source stream's memory is used in place (see OpenSource).
	private RecyclableMemoryStream? _sourceCopy;

    /// <summary>
    ///     Creates a new VCDIFF Encoder from a source stream. The input streams will not be
    ///     closed once this object is disposed.
    /// </summary>
    /// <param name="source">The dictionary (source file).</param>
    /// <param name="target">The target to create the diff from.</param>
    /// <param name="outputStream">The stream to write the diff into.</param>
    /// <param name="options">
    ///     The encoder options. See <see cref="VcEncoderOptions" />. <see cref="VcEncoderOptions.Interleaved" /> and
    ///     <see cref="VcEncoderOptions.ChecksumFormat" /> are ignored: this encoder takes the output format as
    ///     arguments of <see cref="Encode" /> and <see cref="EncodeAsync" />.
    /// </param>
    /// <remarks>
    ///     The source is read from its current position to its end. When <paramref name="source" /> is a
    ///     <see cref="RecyclableMemoryStream" />, or a read-only <see cref="MemoryStream" /> whose buffer is exposable
    ///     (<see cref="MemoryStream.TryGetBuffer" />), its memory is used in place instead of being copied, so
    ///     such a stream must not be modified or disposed until this encoder is disposed.
    /// </remarks>
    public VcEncoder(Stream source, Stream target, Stream outputStream, VcEncoderOptions options)
	{
		this._bytePool = options.BytePoolOrDefault;
		this._manager = options.MemoryStreamManagerOrDefault;
		this._targetData = target;
		this._outputStream = outputStream;

		var dictionary = this.OpenSource(source);
		try
		{
			this._session = new EncoderSession(dictionary, options);
		}
		catch
		{
			this._sourceCopy?.Dispose();
			this._sourceCopy = null;
			throw;
		}
	}

    /// <summary>
    ///     Creates a new VCDIFF Encoder from a source stream. The input streams will not be
    ///     closed once this object is disposed.
    /// </summary>
    /// <param name="source">The dictionary (source file).</param>
    /// <param name="target">The target to create the diff from.</param>
    /// <param name="outputStream">The stream to write the diff into.</param>
    /// <param name="maxBufferSize">The maximum buffer size for window chunking in megabytes (MiB).</param>
    /// <param name="blockSize">The block size to use. Must be an even number; a power of two is recommended.</param>
    /// <param name="chunkSize">The minimum size of a string match that is worth putting into a COPY.</param>
    /// <param name="rollingHash">A reusable <see cref="RollingHash" /> instance the caller owns.</param>
    public VcEncoder
		(Stream source, Stream target, Stream outputStream, int maxBufferSize = 1, int blockSize = 16, int chunkSize = 0, RollingHash? rollingHash = null)
		: this(source, target, outputStream, new VcEncoderOptions {
			MaxBufferSize = maxBufferSize,
			BlockSize = blockSize,
			ChunkSize = chunkSize,
			RollingHash = rollingHash
		})
	{
	}

	private DictionarySource OpenSource(Stream source)
	{
		// The block hash needs random access to the whole dictionary, so it has to be kept in memory.
		// Memory backed streams already are: reference their buffers instead of copying them.
		if (source is RecyclableMemoryStream recyclable)
		{
			var start = recyclable.Position;
			var length = recyclable.Length;
			if (start >= length)
				return new DictionarySource(ReadOnlySequence<byte>.Empty);

			var sequence = recyclable.GetReadOnlySequence().Slice(start);
			recyclable.Position = length; // consume the source as CopyTo would
			return new DictionarySource(sequence);
		}

		// A writable MemoryStream may still be changed by the caller, so only a read-only one is referenced.
		if (source is MemoryStream { CanWrite: false } memory && memory.TryGetBuffer(out var segment) && segment.Array != null)
		{
			var start = memory.Position;
			var length = memory.Length;
			if (start >= length)
				return new DictionarySource(ReadOnlySequence<byte>.Empty);

			var sequence = new ReadOnlySequence<byte>(segment.Array, segment.Offset + (int)start, (int)(length - start));
			memory.Position = length; // consume the source as CopyTo would
			return new DictionarySource(sequence);
		}

		this._sourceCopy = this._manager.GetStream(nameof(VcEncoder));
		try
		{
			source.CopyTo(this._sourceCopy);
			return new DictionarySource(this._sourceCopy.GetReadOnlySequence());
		}
		catch
		{
			this._sourceCopy.Dispose();
			this._sourceCopy = null;
			throw;
		}
	}

    /// <summary>
    ///     Calculate and write a diff for the file.
    /// </summary>
    /// <param name="interleaved">Whether to output in SDCH interleaved diff format.</param>
    /// <param name="checksumFormat">
    ///     Whether to include Adler32 checksums for encoded data windows. If interleaved is true,
    ///     <see cref="ChecksumFormat.Xdelta3" />
    ///     is not supported.
    /// </param>
    /// <param name="progress">Reports an estimate of the encoding progress. Value if 0 to 1.</param>
    /// <returns>
    ///     <see cref="VcDiffResult.SUCCESS" /> if successful, <see cref="VcDiffResult.ERROR" /> if the sourceStream or target
    ///     are zero-length.
    /// </returns>
    /// <exception cref="ArgumentException">If interleaved is true, and <see cref="ChecksumFormat.Xdelta3" /> is chosen.</exception>
    public VcDiffResult Encode(bool interleaved = false, ChecksumFormat checksumFormat = ChecksumFormat.None, IProgress<float>? progress = null)
	{
		EncoderSession.ValidateFormat(interleaved, checksumFormat);
		if (!this.BeginEncode())
			return VcDiffResult.ERROR;

		this._outputStream.Write(EncoderSession.GetFileHeader(interleaved, checksumFormat).Span);

		using var chunker = this._session.CreateChunkEncoder(interleaved, checksumFormat);
		var bufferLength = this.GetWindowLength();
		var buffer = this._bytePool.Rent(bufferLength);
		try
		{
			var window = buffer.AsSpan(0, bufferLength);
			while (this.CanReadTarget)
			{
				var bytesRead = this.FillWindow(window);
				if (bytesRead == 0)
					break;

				chunker.EncodeChunk(window[..bytesRead], this._outputStream);
				progress?.Report((float)this._targetData.Position / this.TargetLength);
			}

			return VcDiffResult.SUCCESS;
		}
		finally
		{
			this._bytePool.Return(buffer, false);
		}
	}

    /// <summary>
    ///     Calculate and write a diff for the file.
    ///     This method isn't fully asynchonous; writes to the output stream are still synchronous.
    ///     It is recommended you use the synchronous <see cref="Encode" /> method for most use cases.
    /// </summary>
    /// <param name="interleaved">Whether to output in SDCH interleaved diff format.</param>
    /// <param name="checksumFormat">
    ///     Whether to include Adler32 checksums for encoded data windows. If interleaved is true,
    ///     <see cref="ChecksumFormat.Xdelta3" />
    ///     is not supported.
    /// </param>
    /// <param name="progress">Reports an estimate of the encoding progress. Value if 0 to 1.</param>
    /// <returns>
    ///     <see cref="VcDiffResult.SUCCESS" /> if successful, <see cref="VcDiffResult.ERROR" /> if the sourceStream or target
    ///     are zero-length.
    /// </returns>
    /// <exception cref="ArgumentException">If interleaved is true, and <see cref="ChecksumFormat.Xdelta3" /> is chosen.</exception>
    public async Task<VcDiffResult> EncodeAsync(bool interleaved = false, ChecksumFormat checksumFormat = ChecksumFormat.None, IProgress<float>? progress = null)
	{
		EncoderSession.ValidateFormat(interleaved, checksumFormat);
		if (!this.BeginEncode())
			return VcDiffResult.ERROR;

		await this._outputStream.WriteAsync(EncoderSession.GetFileHeader(interleaved, checksumFormat));

		using var chunker = this._session.CreateChunkEncoder(interleaved, checksumFormat);
		var bufferLength = this.GetWindowLength();
		var buffer = this._bytePool.Rent(bufferLength);
		try
		{
			var window = new Memory<byte>(buffer, 0, bufferLength);
			while (this.CanReadTarget)
			{
				var read = await this.FillWindowAsync(window);
				if (read == 0)
					break;

				chunker.EncodeChunk(window.Span[..read], this._outputStream);
				progress?.Report((float)this._targetData.Position / this.TargetLength);
			}

			return VcDiffResult.SUCCESS;
		}
		finally
		{
			this._bytePool.Return(buffer, false);
		}
	}

	private long TargetLength => this._targetData.CanRead ? this._targetData.Length : 0;

	private bool CanReadTarget => this._targetData.CanRead && this._targetData.Position < this._targetData.Length;

	// Returns false when there is nothing to encode, otherwise rewinds the target to its start.
	private bool BeginEncode()
	{
		if (this.TargetLength == 0 || this._session.DictionaryLength == 0)
			return false;

		this._targetData.Seek(0, SeekOrigin.Begin);
		return true;
	}

	// Window boundaries must not depend on how many bytes a single Read returns, so fill the whole window.
	private int FillWindow(Span<byte> window)
	{
		var total = 0;
		while (total < window.Length)
		{
			var read = this._targetData.Read(window.Slice(total));
			if (read <= 0)
				break;

			total += read;
		}

		return total;
	}

	private async Task<int> FillWindowAsync(Memory<byte> window)
	{
		var total = 0;
		while (total < window.Length)
		{
			var read = await this._targetData.ReadAsync(window.Slice(total));
			if (read <= 0)
				break;

			total += read;
		}

		return total;
	}

	// A window never spans more than the target, so don't rent a full MaxBufferSize buffer for a small target.
	private int GetWindowLength()
	{
		return (int)Math.Max(1, Math.Min(this._session.WindowSize, this.TargetLength));
	}

    /// <summary>
    ///     Disposes the encoder.
    /// </summary>
    public void Dispose()
	{
		this._session.Dispose();
		this._sourceCopy?.Dispose();
		this._sourceCopy = null;
	}
}
