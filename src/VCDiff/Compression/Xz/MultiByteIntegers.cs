// Portions copyright (c) 2014 Adam Hathcock and the SharpCompress contributors.
// Licensed under the MIT License.

using System.IO;

namespace VCDiff.Compression.Xz;

internal static class MultiByteIntegers
{
	public static ulong ReadXzInteger(this BinaryReader reader, int maxBytes = 9)
	{
		ThrowHelper.ThrowIfNegativeOrZero(maxBytes);

		if (maxBytes > 9) maxBytes = 9;

		var lastByte = reader.ReadByte();
		var output = (ulong)lastByte & 0x7F;

		var i = 0;
		while ((lastByte & 0x80) != 0)
		{
			if (++i >= maxBytes) throw new InvalidFormatException();

			lastByte = reader.ReadByte();
			if (lastByte == 0) throw new InvalidFormatException();

			output |= (ulong)(lastByte & 0x7F) << (i * 7);
		}

		return output;
	}
}