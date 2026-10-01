// Portions copyright (c) 2014 Adam Hathcock and the SharpCompress contributors.
// Licensed under the MIT License.

using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using VCDiff.Compression.Xz.Filters;

namespace VCDiff.Compression.Xz;

internal sealed class XzBlock : ReadOnlyStream
{
	// Largest check field defined by the XZ format (check IDs 0x0D-0x0F => 64 bytes).
	private const int MAX_CHECK_SIZE = 64;
	private const int SHA256_SIZE = 32;

	private readonly ArrayPool<byte> _bytePool;
	private readonly int _checkSize;
	private readonly CheckType _checkType;
	private readonly Stack<BlockFilter> _filters = new();
	private readonly IncrementalHash? _sha256;
	private readonly long _startPosition;
	private byte _blockHeaderSizeByte;
	private uint _crc32 = Crc32.DEFAULT_SEED;
	private ulong _crc64 = Crc64.XZ_SEED;
	private bool _crcChecked;
	private Stream? _decomStream;
	private bool _disposed;
	private bool _endOfStream;
	private bool _headerIsLoaded;
	private int _numFilters;
	private bool _paddingSkipped;
	private bool _streamConnected;
	private int BlockHeaderSize => (this._blockHeaderSizeByte + 1) * 4;

	/// <param name="stream">Input stream; must support getting <see cref="Stream.Position" />.</param>
	/// <param name="checkType">Block check type from the stream header.</param>
	/// <param name="bytePool">Pool for the header buffer and the LZMA window / input buffers.</param>
	public XzBlock(Stream stream, CheckType checkType, ArrayPool<byte>? bytePool = null)
		: base(stream)
	{
		var checkSize = XzHeader.GetCheckSize(checkType);
		if (checkSize < 0 || checkSize > MAX_CHECK_SIZE) throw VcdiffException.UnsupportedXzCheckSize();

		this._checkType = checkType;
		this._checkSize = checkSize;
		this._bytePool = bytePool ?? ArrayPool<byte>.Shared;
		if (checkType == CheckType.SHA256) this._sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

		this._startPosition = stream.Position;
	}

	public override int Read(Span<byte> buffer)
	{
		var bytesRead = 0;
		if (!this._headerIsLoaded) this.LoadHeader();

		if (!this._streamConnected) this.ConnectStream();

		if (!this._endOfStream)
		{
			bytesRead = this._decomStream!.Read(buffer);
			this.UpdateCheck(buffer.Slice(0, bytesRead));
		}

		if (bytesRead != buffer.Length) this._endOfStream = true;

		if (this._endOfStream && !this._paddingSkipped) this.SkipPadding();

		if (this._endOfStream && !this._crcChecked) this.CheckCrc();

		return bytesRead;
	}

	protected override void Dispose(bool disposing)
	{
		if (!this._disposed)
		{
			this._disposed = true;
			if (disposing)
			{
				// Filters own their decoder streams (and the pooled LZMA window / input buffers),
				// but never the base stream of this block.
				foreach (var filter in this._filters) filter.Dispose();

				this._filters.Clear();
				this._decomStream = null;
				this._sha256?.Dispose();
			}
		}

		base.Dispose(disposing);
	}

	private void SkipPadding()
	{
		var bytes = (this.BaseStream.Position - this._startPosition) % 4;
		if (bytes > 0)
		{
			Span<byte> paddingBytes = stackalloc byte[4 - (int)bytes];
			this.BaseStream.ReadExactOrThrow(paddingBytes);
			if (!ReadHelpers.IsAllZero(paddingBytes)) throw VcdiffException.NonNullPaddingBytes();
		}

		this._paddingSkipped = true;
	}

	private void CheckCrc()
	{
		Span<byte> crc = stackalloc byte[MAX_CHECK_SIZE];
		crc = crc.Slice(0, this._checkSize);
		this.BaseStream.ReadExactOrThrow(crc);
		this.VerifyCheck(crc);
		this._crcChecked = true;
	}

	private void UpdateCheck(ReadOnlySpan<byte> bytes)
	{
		if (bytes.IsEmpty || this._checkType == CheckType.NONE) return;

		switch (this._checkType)
		{
			case CheckType.CRC32:
				this._crc32 = Crc32.Update(this._crc32, bytes);
				break;
			case CheckType.CRC64:
				this._crc64 = Crc64.UpdateXz(this._crc64, bytes);
				break;
			case CheckType.SHA256:
				this._sha256!.AppendData(bytes);
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
				if (expected.Length != sizeof(uint) || BinaryPrimitives.ReadUInt32LittleEndian(expected) != ~this._crc32) throw VcdiffException.BlockCheckCorrupt();

				break;
			case CheckType.CRC64:
				if (expected.Length != sizeof(ulong) || BinaryPrimitives.ReadUInt64LittleEndian(expected) != ~this._crc64) throw VcdiffException.BlockCheckCorrupt();

				break;
			case CheckType.SHA256:
				this.FinalizeSha256Check(expected);
				break;
			default:
				throw VcdiffException.UnsupportedXzCheckType();
		}
	}

	private void FinalizeSha256Check(ReadOnlySpan<byte> expected)
	{
		Span<byte> hash = stackalloc byte[SHA256_SIZE];
		if (!this._sha256!.TryGetHashAndReset(hash, out var written) || !expected.SequenceEqual(hash.Slice(0, written))) throw VcdiffException.BlockCheckCorrupt();
	}

	private void ConnectStream()
	{
		// Filters stay in the stack (enumerated in pop order) so Dispose can release them.
		this._decomStream = this.BaseStream;
		foreach (var filter in this._filters)
		{
			filter.SetBaseStream(this._decomStream);
			this._decomStream = filter;
		}

		this._streamConnected = true;
	}

	private void LoadHeader()
	{
		this.ReadHeaderSize();

		// The header is at most 1024 bytes: rent it and parse in place (no memory stream copy).
		var headerLength = this.BlockHeaderSize - 4;
		var headerCache = this._bytePool.Rent(headerLength);
		try
		{
			var header = headerCache.AsSpan(0, headerLength);
			this.CacheHeader(header);

			var reader = new XzSpanReader(header);
			reader.ReadByte(); // skip the header size byte
			this.ReadBlockFlags(ref reader);
			this.ReadFilters(ref reader);
		}
		finally
		{
			this._bytePool.Return(headerCache);
		}

		this._headerIsLoaded = true;
	}

	private void ReadHeaderSize()
	{
		this._blockHeaderSizeByte = this.BaseStream.ReadByteOrThrow();
		if (this._blockHeaderSizeByte == 0) throw new XzIndexMarkerReachedException();
	}

	private void CacheHeader(Span<byte> blockHeaderWithoutCrc)
	{
		blockHeaderWithoutCrc[0] = this._blockHeaderSizeByte;
		this.BaseStream.ReadExactOrThrow(blockHeaderWithoutCrc.Slice(1));

		var crc = this.BaseStream.ReadUInt32LittleEndianOrThrow();
		var calcCrc = Crc32.Compute(blockHeaderWithoutCrc);
		if (crc != calcCrc) throw VcdiffException.BlockHeaderCorrupt();
	}

	private void ReadBlockFlags(ref XzSpanReader reader)
	{
		var blockFlags = reader.ReadByte();
		this._numFilters = (blockFlags & 0x03) + 1;
		var reserved = (byte)(blockFlags & 0x3C);

		if (reserved != 0)
		{
			throw VcdiffException.ReservedXzBytesUsed();
		}

		// Optional compressed / uncompressed sizes: validated as XZ integers, not needed for sequential decoding.
		if ((blockFlags & 0x40) != 0) reader.ReadXzInteger();

		if ((blockFlags & 0x80) != 0) reader.ReadXzInteger();
	}

	private void ReadFilters(ref XzSpanReader reader)
	{
		var nonLastSizeChangers = 0;
		for (var i = 0; i < this._numFilters; i++)
		{
			var filter = BlockFilter.Read(ref reader, this._bytePool);
			this._filters.Push(filter); // pushed first so Dispose releases it even if validation below throws
			if (
				(i + 1 == this._numFilters && !filter.AllowAsLast) || (i + 1 < this._numFilters && !filter.AllowAsNonLast)
			)
				throw VcdiffException.BlockFiltersBadOrder();

			if (filter.ChangesDataSize && i + 1 < this._numFilters) nonLastSizeChangers++;
		}

		if (nonLastSizeChangers > 2)
		{
			throw VcdiffException.TooManySizeChangingFilters();
		}

		var blockHeaderPaddingSize = this.BlockHeaderSize - (4 + reader.Position);
		var blockHeaderPadding = reader.ReadBytes(blockHeaderPaddingSize);
		if (!ReadHelpers.IsAllZero(blockHeaderPadding)) throw VcdiffException.BlockHeaderUnknownFields();
	}
}
