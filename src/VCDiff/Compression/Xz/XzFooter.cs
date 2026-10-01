// Portions copyright (c) 2014 Adam Hathcock and the SharpCompress contributors.
// Licensed under the MIT License.

using System;
using System.Buffers.Binary;
using System.IO;

namespace VCDiff.Compression.Xz;

/// <summary>
///     XZ stream footer: CRC32, backward size, stream flags, magic bytes (12 bytes).
/// </summary>
internal static class XzFooter
{
	private const int SIZE = 12;
	private static ReadOnlySpan<byte> MagicBytes => "YZ"u8;

	/// <summary>
	///     Reads and validates the stream footer against the header's check type and the size of the index that
	///     precedes it.
	/// </summary>
	public static void Read(Stream stream, CheckType checkType, long indexSize)
	{
		Span<byte> footer = stackalloc byte[SIZE];
		stream.ReadExactOrThrow(footer);

		var backwardSizeAndFlags = footer.Slice(4, 6);
		if (BinaryPrimitives.ReadUInt32LittleEndian(footer) != Crc32.Compute(backwardSizeAndFlags)) throw VcdiffException.FooterCorrupt();

		if (!footer.Slice(10, 2).SequenceEqual(MagicBytes)) throw VcdiffException.MagicFooterMissing();

		if (XzHeader.ParseStreamFlags(backwardSizeAndFlags.Slice(4, 2)) != checkType) throw VcdiffException.StreamFooterFlagsMismatch();

		var backwardSize = ((long)BinaryPrimitives.ReadUInt32LittleEndian(backwardSizeAndFlags) + 1) * 4;
		if (backwardSize != indexSize) throw VcdiffException.StreamFooterBackwardSizeMismatch();
	}
}
