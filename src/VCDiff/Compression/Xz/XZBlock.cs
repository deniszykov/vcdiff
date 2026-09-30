// Portions copyright (c) 2014 Adam Hathcock and the SharpCompress contributors.
// Licensed under the MIT License.

using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using VCDiff.Compression.Xz.Filters;

namespace VCDiff.Compression.Xz;

public sealed class XzBlock : XzReadOnlyStream
{
	private readonly ArrayPool<byte> _bytePool;
	private readonly int _checkSize;
	private readonly CheckType _checkType;
	private readonly Stack<BlockFilter> _filters = new();
	private readonly SHA256? _sha256;
	private readonly long _startPosition;
	private byte _blockHeaderSizeByte;
	private uint _crc32 = Crc32.DEFAULT_SEED;
	private ulong _crc64 = Crc64.XZ_SEED;
	private bool _crcChecked;
	private Stream? _decomStream;
	private bool _endOfStream;
	private int _numFilters;
	private bool _paddingSkipped;
	private bool _streamConnected;
	private int BlockHeaderSize => (this._blockHeaderSizeByte + 1) * 4;
	public ulong? CompressedSize { get; private set; }
	public ulong? UncompressedSize { get; private set; }
	private bool HeaderIsLoaded { get; set; }

	public XzBlock(Stream stream, CheckType checkType, int checkSize, ArrayPool<byte>? bytePool = null)
		: base(stream)
	{
		this._checkType = checkType;
		this._checkSize = checkSize;
		this._bytePool = bytePool ?? ArrayPool<byte>.Shared;
		if (checkType == CheckType.SHA256) this._sha256 = SHA256.Create();

		this._startPosition = stream.Position;
	}

	public override int Read(byte[] buffer, int offset, int count)
	{
		var bytesRead = 0;
		if (!this.HeaderIsLoaded) this.LoadHeader();

		if (!this._streamConnected) this.ConnectStream();

		if (!this._endOfStream)
		{
			bytesRead = this._decomStream.NotNull().Read(buffer, offset, count);
			this.UpdateCheck(buffer, offset, bytesRead);
		}

		if (bytesRead != count) this._endOfStream = true;

		if (this._endOfStream && !this._paddingSkipped) this.SkipPadding();

		if (this._endOfStream && !this._crcChecked) this.CheckCrc();

		return bytesRead;
	}

	private void SkipPadding()
	{
		var bytes = (this.BaseStream.Position - this._startPosition) % 4;
		if (bytes > 0)
		{
			var paddingBytes = new byte[4 - bytes];
			this.BaseStream.Read(paddingBytes, 0, paddingBytes.Length);
			if (paddingBytes.Any(b => b != 0)) throw new InvalidFormatException("Padding bytes were non-null");
		}

		this._paddingSkipped = true;
	}

	private void CheckCrc()
	{
		var crc = this._bytePool.Rent(this._checkSize);
		try
		{
			this.BaseStream.ReadExact(crc, 0, this._checkSize);
			this.VerifyCheck(crc.AsSpan().Slice(0, this._checkSize));
			this._crcChecked = true;
		}
		finally
		{
			this._bytePool.Return(crc);
		}
	}

	private void UpdateCheck(byte[] buffer, int offset, int count)
	{
		if (count == 0 || this._checkType == CheckType.NONE) return;

		var bytes = buffer.AsSpan(offset, count);
		switch (this._checkType)
		{
			case CheckType.CRC32:
				this._crc32 = Crc32.Update(this._crc32, bytes);
				break;
			case CheckType.CRC64:
				this._crc64 = Crc64.UpdateXz(this._crc64, bytes);
				break;
			case CheckType.SHA256:
				this._sha256.NotNull().TransformBlock(buffer, offset, count, null, 0);
				break;
		}
	}

	private void VerifyCheck(ReadOnlySpan<byte> expected)
	{
		switch (this._checkType)
		{
			case CheckType.NONE:
				break;
			case CheckType.CRC32:
				this.GetLittleEndianBytes(~this._crc32, expected);
				break;
			case CheckType.CRC64:
				this.GetLittleEndianBytes(~this._crc64, expected);
				break;
			case CheckType.SHA256:
				this.FinalizeSha256Check(expected);
				break;
			default:
				throw new InvalidFormatException("Unsupported XZ check type");
		}
	}

	private void GetLittleEndianBytes(uint value, ReadOnlySpan<byte> expected)
	{
		var bytes = this._bytePool.Rent(sizeof(uint));
		try
		{
			BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
			if (!expected.SequenceEqual(bytes.AsSpan().Slice(0, sizeof(uint)))) throw new InvalidFormatException("Block check corrupt");
		}
		finally
		{
			this._bytePool.Return(bytes);
		}
	}

	private void GetLittleEndianBytes(ulong value, ReadOnlySpan<byte> expected)
	{
		var bytes = this._bytePool.Rent(sizeof(ulong));
		try
		{
			BinaryPrimitives.WriteUInt64LittleEndian(bytes, value);
			if (!expected.SequenceEqual(bytes.AsSpan().Slice(0, sizeof(ulong)))) throw new InvalidFormatException("Block check corrupt");
		}
		finally
		{
			this._bytePool.Return(bytes);
		}
	}

	private void FinalizeSha256Check(ReadOnlySpan<byte> expected)
	{
		this._sha256.NotNull().TransformFinalBlock(Array.Empty<byte>(), 0, 0);
		if (!expected.SequenceEqual(this._sha256.NotNull().Hash)) throw new InvalidFormatException("Block check corrupt");
	}

	private void ConnectStream()
	{
		this._decomStream = this.BaseStream;
		while (this._filters.Any())
		{
			var filter = this._filters.Pop();
			filter.SetBaseStream(this._decomStream);
			this._decomStream = filter;
		}

		this._streamConnected = true;
	}

	private void LoadHeader()
	{
		this.ReadHeaderSize();
		var headerCache = this.CacheHeader();

		using (var cache = new MemoryStream(headerCache))
		using (var cachedReader = new BinaryReader(cache))
		{
			cachedReader.BaseStream.Position = 1; // skip the header size byte
			this.ReadBlockFlags(cachedReader);
			this.ReadFilters(cachedReader);
		}

		this.HeaderIsLoaded = true;
	}

	private void ReadHeaderSize()
	{
		this._blockHeaderSizeByte = (byte)this.BaseStream.ReadByte();
		if (this._blockHeaderSizeByte == 0) throw new XzIndexMarkerReachedException();
	}

	private byte[] CacheHeader()
	{
		var blockHeaderWithoutCrc = new byte[this.BlockHeaderSize - 4];
		blockHeaderWithoutCrc[0] = this._blockHeaderSizeByte;
		var read = this.BaseStream.Read(blockHeaderWithoutCrc, 1, this.BlockHeaderSize - 5);
		if (read != this.BlockHeaderSize - 5) throw new IncompleteArchiveException("Reached end of stream unexpectedly");

		var crc = this.BaseStream.ReadLittleEndianUInt32();
		var calcCrc = Crc32.Compute(blockHeaderWithoutCrc);
		if (crc != calcCrc) throw new InvalidFormatException("Block header corrupt");

		return blockHeaderWithoutCrc;
	}

	private void ReadBlockFlags(BinaryReader reader)
	{
		var blockFlags = reader.ReadByte();
		this._numFilters = (blockFlags & 0x03) + 1;
		var reserved = (byte)(blockFlags & 0x3C);

		if (reserved != 0)
		{
			throw new InvalidFormatException(
				"Reserved bytes used, perhaps an unknown XZ implementation"
			);
		}

		var compressedSizePresent = (blockFlags & 0x40) != 0;
		var uncompressedSizePresent = (blockFlags & 0x80) != 0;

		if (compressedSizePresent) this.CompressedSize = reader.ReadXzInteger();

		if (uncompressedSizePresent) this.UncompressedSize = reader.ReadXzInteger();
	}

	private void ReadFilters(BinaryReader reader, long baseStreamOffset = 0)
	{
		var nonLastSizeChangers = 0;
		for (var i = 0; i < this._numFilters; i++)
		{
			var filter = BlockFilter.Read(reader, this._bytePool);
			if (
				(i + 1 == this._numFilters && !filter.AllowAsLast) || (i + 1 < this._numFilters && !filter.AllowAsNonLast)
			)
				throw new InvalidFormatException("Block Filters in bad order");

			if (filter.ChangesDataSize && i + 1 < this._numFilters) nonLastSizeChangers++;

			filter.ValidateFilter();
			this._filters.Push(filter);
		}

		if (nonLastSizeChangers > 2)
		{
			throw new InvalidFormatException(
				"More than two non-last block filters cannot change stream size"
			);
		}

		var blockHeaderPaddingSize = this.BlockHeaderSize - (4 + (int)(reader.BaseStream.Position - baseStreamOffset));
		var blockHeaderPadding = reader.ReadBytes(blockHeaderPaddingSize);
		if (!blockHeaderPadding.All(b => b == 0)) throw new InvalidFormatException("Block header contains unknown fields");
	}
}