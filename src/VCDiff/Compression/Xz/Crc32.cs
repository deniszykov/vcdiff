// Portions copyright (c) 2014 Adam Hathcock and the SharpCompress contributors.
// Licensed under the MIT License.

using System;

namespace VCDiff.Compression.Xz;

public static class Crc32
{
	public const uint DEFAULT_POLYNOMIAL = 0xedb88320u;
	public const uint DEFAULT_SEED = 0xffffffffu;

	private static uint[]? DefaultTable;

	public static uint Compute(byte[] buffer)
	{
		return Compute(DEFAULT_SEED, buffer);
	}

	public static uint Compute(uint seed, byte[] buffer)
	{
		return Compute(DEFAULT_POLYNOMIAL, seed, buffer);
	}

	public static uint Compute(uint polynomial, uint seed, byte[] buffer)
	{
		return ~CalculateHash(InitializeTable(polynomial), seed, buffer);
	}

	private static uint[] InitializeTable(uint polynomial)
	{
		if (polynomial == DEFAULT_POLYNOMIAL && DefaultTable != null) return DefaultTable;

		var createTable = new uint[256];
		for (var i = 0; i < 256; i++)
		{
			var entry = (uint)i;
			for (var j = 0; j < 8; j++)
			{
				if ((entry & 1) == 1)
					entry = (entry >> 1) ^ polynomial;
				else
					entry >>= 1;
			}

			createTable[i] = entry;
		}

		if (polynomial == DEFAULT_POLYNOMIAL) DefaultTable = createTable;

		return createTable;
	}

	public static uint Update(uint seed, ReadOnlySpan<byte> buffer)
	{
		return CalculateHash(InitializeTable(DEFAULT_POLYNOMIAL), seed, buffer);
	}

	private static uint CalculateHash(uint[] table, uint seed, ReadOnlySpan<byte> buffer)
	{
		var crc = seed;
		var len = buffer.Length;
		for (var i = 0; i < len; i++) crc = (crc >> 8) ^ table[(buffer[i] ^ crc) & 0xff];

		return crc;
	}
}