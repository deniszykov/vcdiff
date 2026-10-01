// Portions copyright (c) 2014 Adam Hathcock and the SharpCompress contributors.
// Licensed under the MIT License.

using System;

namespace VCDiff.Compression.Xz;

/// <summary>
///     CRC-64 (ECMA-182, reflected polynomial 0xC96C5795D7870F42) as used by the XZ CRC64 block check.
/// </summary>
internal static class Crc64
{
	public const ulong XZ_SEED = 0xffffffffffffffff;
	private const ulong XZ_POLYNOMIAL = 0xC96C5795D7870F42;

	private static readonly ulong[] XzTable = CreateTable(XZ_POLYNOMIAL);

	/// <summary>
	///     Feeds <paramref name="buffer" /> into a running (non-finalized) CRC state; start from <see cref="XZ_SEED" />
	///     and complement the final value.
	/// </summary>
	public static ulong UpdateXz(ulong seed, ReadOnlySpan<byte> buffer)
	{
		var table = XzTable;
		var crc = seed;
		foreach (var b in buffer) crc = (crc >> 8) ^ table[(b ^ crc) & 0xff];

		return crc;
	}

	private static ulong[] CreateTable(ulong polynomial)
	{
		var table = new ulong[256];
		for (var i = 0; i < 256; ++i)
		{
			var entry = (ulong)i;
			for (var j = 0; j < 8; ++j)
			{
				if ((entry & 1) == 1)
					entry = (entry >> 1) ^ polynomial;
				else
					entry >>= 1;
			}

			table[i] = entry;
		}

		return table;
	}
}
