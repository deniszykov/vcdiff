// Portions copyright (c) 2014 Adam Hathcock and the SharpCompress contributors.
// Licensed under the MIT License.

using System;

namespace VCDiff.Compression.Xz;

/// <summary>
///     CRC-32 (IEEE 802.3, reflected polynomial 0xEDB88320) as used by XZ headers, indexes, footers and block checks.
/// </summary>
internal static class Crc32
{
	public const uint DEFAULT_SEED = 0xffffffffu;
	private const uint POLYNOMIAL = 0xedb88320u;

	private static readonly uint[] Table = CreateTable();

	/// <summary>Computes the finalized CRC-32 of <paramref name="buffer" />.</summary>
	public static uint Compute(ReadOnlySpan<byte> buffer)
	{
		return ~Update(DEFAULT_SEED, buffer);
	}

	/// <summary>
	///     Feeds <paramref name="buffer" /> into a running (non-finalized) CRC state; start from
	///     <see cref="DEFAULT_SEED" /> and complement the final value.
	/// </summary>
	public static uint Update(uint seed, ReadOnlySpan<byte> buffer)
	{
		var table = Table;
		var crc = seed;
		foreach (var b in buffer) crc = (crc >> 8) ^ table[(b ^ crc) & 0xff];

		return crc;
	}

	private static uint[] CreateTable()
	{
		var table = new uint[256];
		for (var i = 0; i < 256; i++)
		{
			var entry = (uint)i;
			for (var j = 0; j < 8; j++)
			{
				if ((entry & 1) == 1)
					entry = (entry >> 1) ^ POLYNOMIAL;
				else
					entry >>= 1;
			}

			table[i] = entry;
		}

		return table;
	}
}
