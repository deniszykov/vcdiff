// Portions copyright (c) 2014 Adam Hathcock and the SharpCompress contributors.
// Licensed under the MIT License.

using System;

namespace VCDiff.Compression.Xz;

public static class Crc64
{
	public const ulong DEFAULT_SEED = 0x0;

	public const ulong ISO3309_POLYNOMIAL = 0xD800000000000000;
	private const ulong XZ_POLYNOMIAL = 0xC96C5795D7870F42;
	internal const ulong XZ_SEED = 0xffffffffffffffff;
	private static ulong[]? XzTable;

	internal static ulong[]? Table;

	public static ulong Compute(byte[] buffer)
	{
		return Compute(DEFAULT_SEED, buffer);
	}

	public static ulong Compute(ulong seed, byte[] buffer)
	{
		Table ??= CreateTable(ISO3309_POLYNOMIAL);

		return CalculateHash(seed, Table, buffer);
	}

	public static ulong ComputeXz(byte[] buffer)
	{
		return ~UpdateXz(XZ_SEED, buffer);
	}

	public static ulong UpdateXz(ulong seed, ReadOnlySpan<byte> buffer)
	{
		XzTable ??= CreateTable(XZ_POLYNOMIAL);

		return CalculateHash(seed, XzTable, buffer);
	}

	public static ulong CalculateHash(ulong seed, ulong[] table, ReadOnlySpan<byte> buffer)
	{
		var crc = seed;
		var len = buffer.Length;
		for (var i = 0; i < len; i++)
		{
			unchecked
			{
				crc = (crc >> 8) ^ table[(buffer[i] ^ crc) & 0xff];
			}
		}

		return crc;
	}

	public static ulong[] CreateTable(ulong polynomial)
	{
		var createTable = new ulong[256];
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

			createTable[i] = entry;
		}

		return createTable;
	}
}