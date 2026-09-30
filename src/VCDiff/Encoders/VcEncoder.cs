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
	private delegate Task WriteMagicHeader(byte[] writeBytes);

	private static readonly byte[] MagicBytes = { 0xD6, 0xC3, 0xC4, 0x00, 0x00 };
	private static readonly byte[] MagicBytesExtended = { 0xD6, 0xC3, 0xC4, (byte)'S', 0x00 };
	private readonly ArrayPool<byte> _bytePool = ArrayPool<byte>.Shared;
	private readonly DictionarySource oldData;

	// Holds the dictionary read from a source stream, in pooled blocks rather than one large array.
	private RecyclableMemoryStream? _sourceCopy;
	private int blockSize;
	private int bufferSize;
	private int chunkSize;
	private bool disposeRollingHash;
	private RollingHash hasher = null!;
	private Stream outputStream = null!;
	private ByteStreamReader targetData = null!;

    /// <summary>
    ///     Creates a new VCDIFF Encoder from a source stream. The input streams will not be
    ///     closed once this object is disposed.
    /// </summary>
    /// <param name="source">The dictionary (source file).</param>
    /// <param name="target">The target to create the diff from.</param>
    /// <param name="outputStream">The stream to write the diff into.</param>
    /// <param name="options">The encoder options. See <see cref="VcEncoderOptions" />.</param>
    public VcEncoder(Stream source, Stream target, Stream outputStream, VcEncoderOptions options)
	{
		this._bytePool = options.BytePoolOrDefault;

		this._sourceCopy = Pool.MemoryStreamManager.GetStream(nameof(VcEncoder));
		source.CopyTo(this._sourceCopy);
		this.oldData = new DictionarySource(this._sourceCopy.GetReadOnlySequence());

		this.InitializeEncoder(target, outputStream, options);
	}

    /// <summary>
    ///     Creates a new VCDIFF Encoder from a source stream. The input streams will not be
    ///     closed once this object is disposed.
    /// </summary>
    /// <param name="source">The dictionary (source file).</param>
    /// <param name="target">The target to create the diff from.</param>
    /// <param name="outputStream">The stream to write the diff into.</param>
    /// <param name="maxBufferSize">The maximum buffer size for window chunking in megabytes (MiB).</param>
    /// <param name="blockSize">The block size to use. Must be a power of two.</param>
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

    /// <summary>
    ///     Creates a new VCDIFF Encoder from an existing source buffer. The input streams will
    ///     not be closed once this object is disposed.
    /// </summary>
    /// <param name="buffer">The dictionary (source file).</param>
    /// <param name="target">The target to create the diff from.</param>
    /// <param name="outputStream">The stream to write the diff into.</param>
    /// <param name="options">The encoder options. See <see cref="VcEncoderOptions" />.</param>
    public VcEncoder(ByteBuffer buffer, Stream target, Stream outputStream, VcEncoderOptions options)
	{
		this._bytePool = options.BytePoolOrDefault;
		unsafe
		{
			this.oldData = new DictionarySource(buffer.DangerousGetBytePointer(), buffer.Length);
		}

		this.InitializeEncoder(target, outputStream, options);
	}

    /// <summary>
    ///     Creates a new VCDIFF Encoder from an existing source buffer. The input streams will
    ///     not be closed once this object is disposed.
    /// </summary>
    /// <param name="buffer">The dictionary (source file).</param>
    /// <param name="target">The target to create the diff from.</param>
    /// <param name="outputStream">The stream to write the diff into.</param>
    /// <param name="maxBufferSize">The maximum buffer size for window chunking in megabytes (MiB).</param>
    /// <param name="blockSize">The block size to use. Must be a power of two.</param>
    /// <param name="chunkSize">The minimum size of a string match that is worth putting into a COPY.</param>
    /// <param name="rollingHash">A reusable <see cref="RollingHash" /> instance the caller owns.</param>
    public VcEncoder
		(ByteBuffer buffer, Stream target, Stream outputStream, int maxBufferSize = 1, int blockSize = 16, int chunkSize = 0, RollingHash? rollingHash = null)
		: this(buffer, target, outputStream, new VcEncoderOptions {
			MaxBufferSize = maxBufferSize,
			BlockSize = blockSize,
			ChunkSize = chunkSize,
			RollingHash = rollingHash
		})
	{
	}

	private void InitializeEncoder(Stream target, Stream outputStream, VcEncoderOptions options)
	{
		var maxBufferSize = options.MaxBufferSize;
		if (maxBufferSize <= 0)
			maxBufferSize = 1;

		this.bufferSize = maxBufferSize * 1024 * 1024;
		if (target.Length <= maxBufferSize)
			this.bufferSize = (int)target.Length;

		this.blockSize = options.BlockSize;
		this.chunkSize = options.ChunkSize < 2 ? this.blockSize * 2 : options.ChunkSize;
		this.targetData = new ByteStreamReader(target, this._bytePool);
		this.outputStream = outputStream;

		var rollingHash = options.RollingHash;
		if (rollingHash == null)
		{
			this.disposeRollingHash = true;
			this.hasher = new RollingHash(this.blockSize);
		}
		else
			this.hasher = rollingHash;

		if (this.hasher.WindowSize != this.blockSize)
			throw new ArgumentException("Supplied RollingHash instance has a different window size than blocksize!");

		if (this.blockSize % 2 != 0 || this.chunkSize < 2 || this.chunkSize < 2 * this.blockSize)
			throw new ArgumentException($"{this.blockSize} can not be less than 2 or twice the blocksize of the dictionary {this.blockSize}.");
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
		Task WriteBytes(byte[] bytes)
		{
			this.outputStream.Write(bytes);
			return Task.CompletedTask;
		}


		this.ValidateParameters(interleaved, checksumFormat);
		if (!this.Encode_Init(interleaved, checksumFormat, WriteBytes).Result)
			return VcDiffResult.ERROR;

		// Read in all the dictionary it is the only thing that needs to be
		this.Encode_Setup(interleaved, checksumFormat, out var chunker, out var buffer, out var bufferLength);
		try
		{
			var buf = new Memory<byte>(buffer, 0, bufferLength);
			var bufSpan = buf.Span;
			while (this.targetData.CanRead)
			{
				var bytesRead = this.targetData.ReadBytesIntoBuf(bufSpan);
				using var ntarget = new ByteBuffer(buf[..bytesRead]);
				chunker.EncodeChunk(ntarget, this.outputStream);
				progress?.Report((float)this.targetData.Position / this.targetData.Length);
			}

			return VcDiffResult.SUCCESS;
		}
		finally
		{
			chunker.Dispose();
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
		this.ValidateParameters(interleaved, checksumFormat);
		if (!await this.Encode_Init(interleaved, checksumFormat, async bytes => await this.outputStream.WriteAsync(bytes)))
			return VcDiffResult.ERROR;

		//read in all the dictionary it is the only thing that needs to be
		this.Encode_Setup(interleaved, checksumFormat, out var chunker, out var buffer, out var bufferLength);
		try
		{
			var buf = new Memory<byte>(buffer, 0, bufferLength);
			while (this.targetData.CanRead)
			{
				var read = await this.targetData.ReadBytesIntoBufAsync(buf);
				using var ntarget = new ByteBuffer(buf[..read]);
				chunker.EncodeChunk(ntarget, this.outputStream);
				progress?.Report((float)this.targetData.Position / this.targetData.Length);
			}

			return VcDiffResult.SUCCESS;
		}
		finally
		{
			chunker.Dispose();
			this._bytePool.Return(buffer, false);
		}
	}

	private async Task<bool> Encode_Init(bool interleaved, ChecksumFormat checksumFormat, WriteMagicHeader writeBytes)
	{
		if (this.targetData.Length == 0 || this.oldData.Length == 0)
			return false;

		this.targetData.Position = 0;

		// file header
		// write magic bytes
		if (!interleaved && checksumFormat != ChecksumFormat.SDCH)
			await writeBytes(MagicBytes);
		else
			await writeBytes(MagicBytesExtended);

		return true;
	}

	private void ValidateParameters(bool interleaved, ChecksumFormat checksumFormat)
	{
		if (interleaved && checksumFormat == ChecksumFormat.Xdelta3)
			throw new ArgumentException("Interleaved diffs can not have an xdelta3 checksum!");
	}

	private void Encode_Setup(bool interleaved, ChecksumFormat checksumFormat, out ChunkEncoder chunkEncoder, out byte[] buffer, out int bufferLength)
	{
		var dictionary = new BlockHash(this.oldData, this.hasher, this.blockSize);
		dictionary.AddAllBlocks();

		chunkEncoder = new ChunkEncoder(dictionary, this.oldData.Length, this.hasher, checksumFormat, interleaved, this.chunkSize);
		buffer = this._bytePool.Rent(this.bufferSize);
		bufferLength = this.bufferSize;
	}

    /// <summary>
    ///     Disposes the encoder.
    /// </summary>
    public void Dispose()
	{
		this.oldData?.Dispose();
		this._sourceCopy?.Dispose();
		this._sourceCopy = null;
		this.targetData?.Dispose();
		if (this.disposeRollingHash)
			this.hasher.Dispose();
	}
}