// Copyright (c) Denis Zykov and contributors (DeepSeek, ClaudeCode).
// Licensed under the Apache License, Version 2.0.

using System;
using Microsoft.IO;
using VCDiff.Shared;

namespace VCDiff.Encoders;

/// <summary>
///     The state shared by <see cref="VcdiffEncoder" /> and <see cref="VcdiffSpanEncoder" />: validated options,
///     the dictionary, its <see cref="BlockHash" /> and the <see cref="RabinKarpHash" /> used to build and probe it.
/// </summary>
internal sealed class EncoderSession : IDisposable
{
	private const int MEBIBYTE = 1024 * 1024;

	/// <summary>
	///     The largest <see cref="VcdiffEncoderOptions.MaxWindowSizeMiB" />, in MiB, whose window size still fits an <see cref="int" />.
	/// </summary>
	private const int MAX_WINDOW_SIZE_MIB = int.MaxValue / MEBIBYTE;

	private readonly int _blockSize;
	private readonly ISourceReader dictionaryReader;
	private readonly RabinKarpHash _hasher;
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
	public EncoderSession(ISourceReader dictionaryReader, VcdiffEncoderOptions options)
	{
		this.dictionaryReader = dictionaryReader;
		try
		{
			var maxWindowSizeMiB = options.MaxWindowSizeMiB;
			if (maxWindowSizeMiB <= 0)
				maxWindowSizeMiB = 1;
			if (maxWindowSizeMiB > MAX_WINDOW_SIZE_MIB)
				throw VcdiffException.MaxWindowSizeMiBExceeded(MAX_WINDOW_SIZE_MIB);

			var blockSize = options.BlockSize;
			if (blockSize < 2 || blockSize % 2 != 0)
				throw VcdiffException.BlockSizeInvalid(blockSize);

			var minMatchSize = options.MinMatchSize < 2 ? blockSize * 2 : options.MinMatchSize;
			if (minMatchSize < 2 * blockSize)
				throw VcdiffException.MinMatchSizeTooSmall(minMatchSize, blockSize);

			var rabinKarpHash = options.RabinKarpHash;
			if (rabinKarpHash != null && rabinKarpHash.BlockSize != blockSize)
				throw VcdiffException.RabinKarpHashBlockSizeMismatch();

			this.WindowSize = maxWindowSizeMiB * MEBIBYTE;
			this._blockSize = blockSize;
			this._minMatchSize = minMatchSize;
			this._manager = options.MemoryStreamManagerOrDefault;
			this._ownsHasher = rabinKarpHash == null;
			this._hasher = rabinKarpHash ?? new RabinKarpHash(blockSize);
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
	public static void ValidateFormat(bool interleaved, WindowChecksumFormat checksumFormat)
	{
		if (interleaved && checksumFormat == WindowChecksumFormat.Xdelta3)
			throw VcdiffException.InterleavedXdelta3ChecksumNotSupported();
	}

	/// <summary>
	///     The VCDIFF file header (magic bytes and header indicator) for the given output format.
	/// </summary>
	public static ReadOnlyMemory<byte> GetFileHeader(bool interleaved, WindowChecksumFormat checksumFormat)
	{
		return FileHeader.Get(interleaved || checksumFormat == WindowChecksumFormat.Sdch);
	}

	/// <summary>
	///     Creates a window encoder over this session's dictionary. The shared <see cref="BlockHash" /> is created on the
	///     first call; its tables are allocated and filled lazily by the returned encoder (on the first encoded window or
	///     on <see cref="ChunkEncoder.SetSourceSegment" />), so a caller that restricts the source segment before encoding
	///     never indexes the whole dictionary. The returned encoder must be disposed before this session.
	/// </summary>
	public ChunkEncoder CreateChunkEncoder(bool interleaved, WindowChecksumFormat checksumFormat)
	{
		if (this._disposed)
			throw new ObjectDisposedException(nameof(EncoderSession));

		this._blockHash ??= new BlockHash(this.dictionaryReader, this._hasher, this._blockSize);

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
