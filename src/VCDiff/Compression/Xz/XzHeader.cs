// Portions copyright (c) 2014 Adam Hathcock and the SharpCompress contributors.
// Licensed under the MIT License.

using System;
using System.Buffers.Binary;
using System.IO;

namespace VCDiff.Compression.Xz;

/// <summary>
///     XZ stream header: magic bytes, stream flags, CRC32 of the flags (12 bytes).
/// </summary>
internal static class XzHeader
{
	private const int SIZE = 12;
	private static ReadOnlySpan<byte> MagicHeader => new byte[] { 0xFD, 0x37, 0x7A, 0x58, 0x5A, 0x00 };

	/// <summary>Reads and validates the stream header, returning the block check type it declares.</summary>
	public static CheckType Read(Stream stream)
	{
		Span<byte> header = stackalloc byte[SIZE];
		stream.ReadExactOrThrow(header);
		if (!header.Slice(0, MagicHeader.Length).SequenceEqual(MagicHeader)) throw VcdiffException.InvalidXzStream();

		var streamFlags = header.Slice(6, 2);
		if (BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(8)) != Crc32.Compute(streamFlags)) throw VcdiffException.StreamHeaderCorrupt();

		return ParseStreamFlags(streamFlags);
	}

	/// <summary>Validates the two stream-flag bytes (shared by header and footer) and returns the check type.</summary>
	public static CheckType ParseStreamFlags(ReadOnlySpan<byte> streamFlags)
	{
		if (streamFlags[0] != 0 || (streamFlags[1] & 0xF0) != 0) throw VcdiffException.UnknownXzStreamVersion();

		return (CheckType)(streamFlags[1] & 0x0F);
	}

	/// <summary>Size in bytes of the per-block check field for <paramref name="checkType" />.</summary>
	public static int GetCheckSize(CheckType checkType)
	{
		var id = (int)checkType;
		return id == 0 ? 0 : 4 << ((id + 2) / 3 - 1);
	}
}
