// Portions copyright (c) 2014 Adam Hathcock and the SharpCompress contributors.
// Licensed under the MIT License.

using System;
using System.IO;

namespace VCDiff.Compression.Xz;

/// <summary>
///     XZ index: indicator byte, record count, (unpadded size, uncompressed size) records, padding, CRC32.
/// </summary>
internal static class XzIndex
{
	/// <summary>
	///     Reads past the index, validating its structure and CRC32. The records themselves are not needed by a
	///     sequential decoder and are discarded.
	/// </summary>
	/// <param name="stream">Stream positioned right after the index indicator byte (already consumed).</param>
	/// <returns>The size in bytes of the whole index (indicator through CRC32), for the footer's backward size.</returns>
	public static long Skip(Stream stream)
	{
		// The 0x00 indicator was consumed by the block reader (it is how the end of the blocks is detected).
		Span<byte> indicator = stackalloc byte[1];
		var crc = Crc32.Update(Crc32.DEFAULT_SEED, indicator);
		long size = 1;

		var numberOfRecords = stream.ReadXzInteger(ref crc, out var length);
		size += length;
		for (ulong i = 0; i < numberOfRecords; i++)
		{
			stream.ReadXzInteger(ref crc, out length); // unpadded size
			size += length;
			stream.ReadXzInteger(ref crc, out length); // uncompressed size
			size += length;
		}

		var paddingSize = (int)((4 - size % 4) % 4);
		if (paddingSize > 0)
		{
			Span<byte> padding = stackalloc byte[3];
			padding = padding.Slice(0, paddingSize);
			stream.ReadExactOrThrow(padding);
			if (!ReadHelpers.IsAllZero(padding)) throw VcdiffException.NonNullPaddingBytes();

			crc = Crc32.Update(crc, padding);
			size += paddingSize;
		}

		if (stream.ReadUInt32LittleEndianOrThrow() != ~crc) throw VcdiffException.IndexCorrupt();

		return size + sizeof(uint);
	}
}
