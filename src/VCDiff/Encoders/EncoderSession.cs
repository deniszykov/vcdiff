// Copyright (c) Denis Zykov and contributors (DeepSeek, ClaudeCode).
// Licensed under the Apache License, Version 2.0.

using System;
using Microsoft.IO;
using VCDiff.Shared;

namespace VCDiff.Encoders;

/// <summary>
///     The state shared by <see cref="VcEncoder" /> and <see cref="VcDiffEncoder" />: validated options,
///     the dictionary, its <see cref="BlockHash" /> and the <see cref="RollingHash" /> used to build and probe it.
/// </summary>
internal sealed class EncoderSession : IDisposable
{
	private const int MEBIBYTE = 1024 * 1024;

	/// <summary>
	///     The largest <see cref="VcEncoderOptions.MaxBufferSize" />, in MiB, whose window size still fits an <see cref="int" />.
	/// </summary>
	private const int MAX_BUFFER_SIZE_MIB = int.MaxValue / MEBIBYTE;

	private readonly int _blockSize;
	private readonly IDictionaryReader dictionaryReader;
	private readonly RollingHash _hasher;
	private readonly RecyclableMemoryStreamManager _manager;
	private readonly int _minMatchSize;
	private readonly bool _ownsHasher;

	// Built on first use and shared by every ChunkEncoder of this session.
	private BlockHash? _blockHash;
	private bool _disposed;

	/// <summary>
	///     The size of a target window in bytes.
	/// </summary>
	public int WindowSize { get; }

	/// <summary>
	///     The length of the dictionary in bytes.
	/// </summary>
	public long DictionaryLength => this.dictionaryReader.Length;

	/// <summary>
	///     Validates <paramref name="options" /> and creates a session over <paramref name="dictionaryReader" />.
	/// </summary>
	/// <param name="dictionaryReader">The dictionary. Ownership is taken, it is disposed even when this constructor throws.</param>
	/// <param name="options">The encoder options.</param>
	public EncoderSession(IDictionaryReader dictionaryReader, VcEncoderOptions options)
	{
		this.dictionaryReader = dictionaryReader;
		try
		{
			var maxBufferSize = options.MaxBufferSize;
			if (maxBufferSize <= 0)
				maxBufferSize = 1;
			if (maxBufferSize > MAX_BUFFER_SIZE_MIB)
				throw VcdiffException.MaxBufferSizeExceeded(MAX_BUFFER_SIZE_MIB);

			var blockSize = options.BlockSize;
			if (blockSize < 2 || blockSize % 2 != 0)
				throw VcdiffException.BlockSizeInvalid(blockSize);

			var minMatchSize = options.ChunkSize < 2 ? blockSize * 2 : options.ChunkSize;
			if (minMatchSize < 2 * blockSize)
				throw VcdiffException.ChunkSizeTooSmall(minMatchSize, blockSize);

			var rollingHash = options.RollingHash;
			if (rollingHash != null && rollingHash.WindowSize != blockSize)
				throw VcdiffException.RollingHashWindowMismatch();

			this.WindowSize = maxBufferSize * MEBIBYTE;
			this._blockSize = blockSize;
			this._minMatchSize = minMatchSize;
			this._manager = options.MemoryStreamManagerOrDefault;
			this._ownsHasher = rollingHash == null;
			this._hasher = rollingHash ?? new RollingHash(blockSize);
		}
		catch
		{
			dictionaryReader.Dispose();
			throw;
		}
	}

	/// <summary>
	///     Throws when <paramref name="interleaved" /> and <paramref name="checksumFormat" /> can not be combined.
	/// </summary>
	public static void ValidateFormat(bool interleaved, ChecksumFormat checksumFormat)
	{
		if (interleaved && checksumFormat == ChecksumFormat.Xdelta3)
			throw VcdiffException.InterleavedXdelta3ChecksumNotSupported();
	}

	/// <summary>
	///     The VCDIFF file header (magic bytes and header indicator) for the given output format.
	/// </summary>
	public static ReadOnlyMemory<byte> GetFileHeader(bool interleaved, ChecksumFormat checksumFormat)
	{
		return FileHeader.Get(interleaved || checksumFormat == ChecksumFormat.SDCH);
	}

	/// <summary>
	///     Creates a window encoder over this session's dictionary. The dictionary is hashed on the first call.
	///     The returned encoder must be disposed before this session.
	/// </summary>
	public ChunkEncoder CreateChunkEncoder(bool interleaved, ChecksumFormat checksumFormat)
	{
		if (this._disposed)
			throw new ObjectDisposedException(nameof(EncoderSession));

		if (this._blockHash == null)
		{
			var blockHash = new BlockHash(this.dictionaryReader, this._hasher, this._blockSize);
			try
			{
				blockHash.AddAllBlocks();
			}
			catch
			{
				blockHash.Dispose();
				throw;
			}

			this._blockHash = blockHash;
		}

		return new ChunkEncoder(this._blockHash, this.dictionaryReader.Length, this._hasher, checksumFormat, interleaved, this._minMatchSize, this._manager);
	}

	public void Dispose()
	{
		if (this._disposed)
			return;

		this._disposed = true;
		this._blockHash?.Dispose();
		this._blockHash = null;
		this.dictionaryReader.Dispose();
		if (this._ownsHasher)
			this._hasher.Dispose();
	}
}
