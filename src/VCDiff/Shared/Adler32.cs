// Copyright (c) Metric and the Snowflake Authors.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace VCDiff.Shared;

internal class Adler32
{
    /// <summary>
    ///     Zlib implementation of the Adler32 Hash
    /// </summary>
    private const uint BASE = 65521;

	private const int BLOCK_SIZE = 1 << 5;

	private const uint NMAX = 5552;
	private const byte S1_O32 = (1 << 6) | (0 << 4) | (3 << 2) | 2;

	private const byte S23_O1 = (2 << 6) | (3 << 4) | (0 << 2) | 1;

	private static readonly Vector256<sbyte> Tap;

	private static readonly Vector128<sbyte> Tap1;
	private static readonly Vector128<sbyte> Tap2;

	static Adler32()
	{
		if (Avx2.IsSupported)
		{
			Tap = Vector256.Create(32, 31, 30, 29, 28, 27, 26, 25, 24, 23, 22, 21, 20,
				19, 18, 17, 16, 15, 14, 13, 12, 11, 10, 9, 8, 7, 6, 5, 4, 3, 2, 1);
		}
		else if (Sse2.IsSupported)
		{
			Tap1 =
				Vector128.Create(32, 31, 30, 29, 28, 27, 26, 25, 24, 23, 22, 21, 20, 19, 18, 17);
			Tap2 = Vector128.Create(16, 15, 14, 13, 12, 11, 10, 9, 8, 7, 6, 5, 4, 3, 2, 1);
		}
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private static void Do(ref uint adler, ref uint sum2, ReadOnlySpan<byte> buffer, int i, int times)
	{
		while (times-- > 0)
		{
			adler += buffer[i++];
			sum2 += adler;
		}
	}

    /// <summary>
    ///     SSSE3 Version of Adler32
    ///     https://chromium.googlesource.com/chromium/src/third_party/zlib/+/master/adler32_simd.c
    /// </summary>
    /// <param name="adler"></param>
    /// <param name="buff"></param>
    /// <returns></returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
	private static unsafe uint HashSsse3(uint adler, ReadOnlySpan<byte> buff)
	{
		fixed (byte* buffAddr = buff)
		{
			var s1 = adler & 0xffff;
			var s2 = adler >> 16;

			var dof = 0;
			var len = buff.Length;
			var blocks = len / BLOCK_SIZE;
			len -= blocks * BLOCK_SIZE;

			while (blocks > 0)
			{
				var n = NMAX / BLOCK_SIZE;
				if (n > blocks) n = (uint)blocks;
				blocks -= (int)n;

				var zero = Vector128<byte>.Zero;
				var ones = Vector128.Create((short)1);

				//  Process n blocks of data. At most NMAX data bytes can be processed before s2 must be reduced modulo BASE.
				var vPs = Vector128.Create(0, 0, 0, s1 * n);
				var vS2 = Vector128.Create(0, 0, 0, s2);
				var vS1 = Vector128<uint>.Zero;

				do
				{
					// Load 32 input bytes.
					var bytes1 = Sse2.LoadVector128(buffAddr + dof);
					var bytes2 = Sse2.LoadVector128(buffAddr + dof + 16);

					// Add previous block byte sum to v_ps. 
					vPs = Sse2.Add(vPs, vS1);

					// Horizontally add the bytes for s1, multiply-adds the bytes by[32, 31, 30, ... ] for s2.
					vS1 = Sse2.Add(vS1, Sse2.SumAbsoluteDifferences(bytes1, zero).AsUInt32());
					var mad1 = Ssse3.MultiplyAddAdjacent(bytes1, Tap1);
					vS2 = Sse2.Add(vS2, Sse2.MultiplyAddAdjacent(mad1, ones).AsUInt32());
					vS1 = Sse2.Add(vS1, Sse2.SumAbsoluteDifferences(bytes2, zero).AsUInt32());
					var mad2 = Ssse3.MultiplyAddAdjacent(bytes2, Tap2);
					vS2 = Sse2.Add(vS2, Sse2.MultiplyAddAdjacent(mad2, ones).AsUInt32());
					dof += BLOCK_SIZE;
				} while (--n > 0);

				vS2 = Sse2.Add(vS2, Sse2.ShiftLeftLogical(vPs, 5));

				//  Sum epi32 ints v_s1(s2) and accumulate in s1(s2).

				// Shuffling 2301 then 1032 achieves the same thing as described here.
				// https://stackoverflow.com/questions/6996764/fastest-way-to-do-horizontal-float-vector-sum-on-x86
				// Vector128<uint> hi64 = Sse2.Shuffle(v_s1, S1O32);
				// Vector128<uint> sum64 = Sse2.Add(hi64, v_s1);
				// Vector128<uint> hi32 = Sse2.ShuffleLow(sum64.AsUInt16(), S1O32).AsUInt32();
				// Vector128<uint> sum32 = Sse2.Add(sum64, hi32);

				vS1 = Sse2.Add(vS1, Sse2.Shuffle(vS1, S23_O1));
				vS1 = Sse2.Add(vS1, Sse2.Shuffle(vS1, S1_O32));

				s1 += Sse2.ConvertToUInt32(vS1);

				vS2 = Sse2.Add(vS2, Sse2.Shuffle(vS2, S23_O1));
				vS2 = Sse2.Add(vS2, Sse2.Shuffle(vS2, S1_O32));
				s2 = Sse2.ConvertToUInt32(vS2);

				// Reduce
				s1 %= BASE;
				s2 %= BASE;
			}

			// Handle leftover data
			if (len > 0)
			{
				while (len >= 16)
				{
					len -= 16;
					Do(ref s1, ref s2, buff, dof, 16);
					dof += 16;
				}

				while (len-- > 0)
				{
					s1 += buffAddr[dof++];
					s2 += s1;
				}

				if (s1 >= BASE)
					s1 -= BASE;
				s2 %= BASE;
			}

			/*
			* Return the recombined sums.
			*/
			return s1 | (s2 << 16);
		}
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
	private static unsafe uint HashAvx2(uint adler, ReadOnlySpan<byte> buff)
	{
		fixed (byte* buffAddr = buff)
		{
			var s1 = adler & 0xffff;
			var s2 = adler >> 16;

			var dof = 0;
			var len = buff.Length;
			var blocks = len / BLOCK_SIZE;
			len -= blocks * BLOCK_SIZE;

			while (blocks > 0)
			{
				var n = NMAX / BLOCK_SIZE;
				if (n > blocks) n = (uint)blocks;
				blocks -= (int)n;

				var zero = Vector256<byte>.Zero;
				var ones = Vector256.Create((short)1);

				//  Process n blocks of data. At most NMAX data bytes can be processed before s2 must be reduced modulo BASE.
				var vPs = Vector256.Create(0, 0, 0, 0, 0, 0, 0, s1 * n);
				var vS2 = Vector256.Create(0, 0, 0, 0, 0, 0, 0, s2);
				var vS1 = Vector256<uint>.Zero;

				do
				{
					// Load 32 input bytes.
					var bytes = Avx.LoadVector256(buffAddr + dof);

					// Add previous block byte sum to v_ps. 
					vPs = Avx2.Add(vPs, vS1);

					// Horizontally add the bytes for s1, multiply-adds the bytes by[32, 31, 30, ... ] for s2.
					vS1 = Avx2.Add(vS1, Avx2.SumAbsoluteDifferences(bytes, zero).AsUInt32());
					var mad = Avx2.MultiplyAddAdjacent(bytes, Tap);
					vS2 = Avx2.Add(vS2, Avx2.MultiplyAddAdjacent(mad, ones).AsUInt32());

					dof += BLOCK_SIZE;
				} while (--n > 0);

				vS2 = Avx2.Add(vS2, Avx2.ShiftLeftLogical(vPs, 5));

				//  Sum epi32 ints v_s1(s2) and accumulate in s1(s2).

				var v128S1 = Sse2.Add(Avx2.ExtractVector128(vS1, 0), Avx2.ExtractVector128(vS1, 1));
				v128S1 = Sse2.Add(v128S1, Sse2.Shuffle(v128S1, S23_O1));
				v128S1 = Sse2.Add(v128S1, Sse2.Shuffle(v128S1, S1_O32));
				s1 += Sse2.ConvertToUInt32(v128S1);

				//// sum v_s2
				var v128S2 = Sse2.Add(Avx2.ExtractVector128(vS2, 0), Avx2.ExtractVector128(vS2, 1));
				v128S2 = Sse2.Add(v128S2, Sse2.Shuffle(v128S2, S23_O1));
				v128S2 = Sse2.Add(v128S2, Sse2.Shuffle(v128S2, S1_O32));
				s2 = Sse2.ConvertToUInt32(v128S2);

				// Reduce
				s1 %= BASE;
				s2 %= BASE;
			}

			// Handle leftover data
			if (len > 0)
			{
				while (len >= 16)
				{
					len -= 16;
					Do(ref s1, ref s2, buff, dof, 16);
					dof += 16;
				}

				while (len-- > 0)
				{
					s1 += buffAddr[dof++];
					s2 += s1;
				}

				if (s1 >= BASE)
					s1 -= BASE;
				s2 %= BASE;
			}

			/*
			* Return the recombined sums.
			*/
			return s1 | (s2 << 16);
		}
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public static uint Hash(uint adler, ReadOnlySpan<byte> buff)
	{
		var len = (uint)buff.Length;
		if (len == 0) return 1;

		if (len == 1)
		{
			var sum2 = (adler >> 16) & 0xffff;
			adler &= 0xffff;

			adler += buff[0];
			if (adler >= BASE) adler -= BASE;

			sum2 += adler;
			if (sum2 >= BASE) sum2 -= BASE;

			return adler | (sum2 << 16);
		}

		if (len < 16)
		{
			var sum2 = (adler >> 16) & 0xffff;
			adler &= 0xffff;

			for (var i = 0; i < len; i++)
			{
				adler += buff[i];
				sum2 += adler;
			}

			if (adler >= BASE) adler -= BASE;

			sum2 %= BASE;
			return adler | (sum2 << 16);
		}

		if (Avx2.IsSupported) return HashAvx2(adler, buff);
		if (Ssse3.IsSupported) return HashSsse3(adler, buff);

		unsafe
		{
			fixed (byte* bufPtr = buff)
			{
				var sum2 = (adler >> 16) & 0xffff;
				adler &= 0xffff;

				var dof = 0;
				while (len >= NMAX)
				{
					len -= NMAX;
					var n = NMAX / 16;
					do
					{
						Do(ref adler, ref sum2, buff, dof, 16);
						dof += 16;
					} while (--n > 0);

					adler %= BASE;
					sum2 %= BASE;
				}

				if (len > 0)
				{
					while (len >= 16)
					{
						len -= 16;
						Do(ref adler, ref sum2, buff, dof, 16);
						dof += 16;
					}

					while (len-- > 0)
					{
						adler += bufPtr[dof++];
						sum2 += adler;
					}

					adler %= BASE;
					sum2 %= BASE;
				}

				return adler | (sum2 << 16);
			}
		}
	}
}