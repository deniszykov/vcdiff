// Copyright (c) Metric and the Snowflake Authors.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace VCDiff.Encoders;

/// <summary>
///     A rolling hasher for <see cref="VcdiffEncoder" /> and <see cref="VcdiffSpanEncoder" />.
///     A <see cref="RabinKarpHash" /> may be reused across encoders with the same block size (see <see cref="VcdiffEncoderOptions.RabinKarpHash" />).
/// </summary>
public class RabinKarpHash : IDisposable
{
	private const int K_BASE = 1 << 23;
	private const int K_MULT = 257;
	private const byte S1_O32 = (1 << 6) | (0 << 4) | (3 << 2) | 2;

	private const byte S23_O1 = (2 << 6) | (3 << 4) | (0 << 2) | 1;
	private const byte SO123 = (0 << 6) | (1 << 4) | (2 << 2) | 3;
	private readonly int[] kMultFactors;
	private readonly unsafe int* kMultFactorsPtr;
	private readonly ulong multiplier;

	private readonly ulong[] removeTable;

	private readonly Vector256<int> vShuf;
	private MemoryHandle kMultFactorsHandle;

    /// <summary>
    ///     The number of bytes hashed at a time (the block size).
    /// </summary>
    public int BlockSize { get; }
    /// <summary>
    ///     Manually creates a rolling hash instance for use with a <see cref="VcdiffEncoder" />.
    ///     This object must be disposed because it allocates pinned memory that will never be garbage collected
    ///     if it is not disposed.
    /// </summary>
    /// <param name="blockSize">The number of bytes hashed at a time (the block size).</param>
    public RabinKarpHash(int blockSize)
	{
		this.vShuf = Vector256.Create(7, 6, 5, 4, 3, 2, 1, 0);
		this.BlockSize = blockSize;
		this.removeTable = new ulong[256];
		this.kMultFactors = new int[blockSize];
		this.kMultFactorsHandle = this.kMultFactors.AsMemory().Pin();
		unsafe
		{
			this.kMultFactorsPtr = (int*)this.kMultFactorsHandle.Pointer;
		}

		this.multiplier = 1;

		for (var i = 0; i < blockSize - 1; ++i)
		{
			this.kMultFactors[i] = (int)this.multiplier;
			this.multiplier = (this.multiplier * K_MULT) & (K_BASE - 1);
		}

		this.kMultFactors[blockSize - 1] = (int)this.multiplier;

		ulong byteTimes = 0;
		for (var i = 0; i < 256; ++i)
		{
			// Get the inverse of the modBase
			this.removeTable[i] = (0 - byteTimes) & (K_BASE - 1);
			byteTimes = (byteTimes + this.multiplier) & (K_BASE - 1);
		}
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private unsafe ulong HashAvx2(byte* buf, int len)
	{
		ulong h = 0;

		var vPs = Vector256<int>.Zero;

		var i = 0;
		for (var j = len - i - 1; len - i >= 8; i += 8, j = len - i - 1)
		{
			var cV = Avx.LoadVector256(&this.kMultFactorsPtr[j - 7]);
			cV = Avx2.PermuteVar8x32(cV, this.vShuf);

			// Loads exactly the 8 bytes hashed: a 16 byte load would read past the end of the block.
			var sV = Avx2.ConvertToVector256Int32(buf + i);

			vPs = Avx2.Add(vPs, Avx2.MultiplyLow(cV, sV));
		}

		var v128S1 = Sse2.Add(Avx2.ExtractVector128(vPs, 0), Avx2.ExtractVector128(vPs, 1));
		v128S1 = Sse2.Add(v128S1, Sse2.Shuffle(v128S1, S23_O1));
		v128S1 = Sse2.Add(v128S1, Sse2.Shuffle(v128S1, S1_O32));
		h += Sse2.ConvertToUInt32(v128S1.AsUInt32());

		for (; i < len; i++)
		{
			var index = len - i - 1;
			ulong c = (uint)this.kMultFactors[index];
			h += c * buf[i];
		}

		return h & (K_BASE - 1);
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private unsafe ulong HashSse(byte* buf, int len)
	{
		ulong h = 0;
		var vPs = Vector128<int>.Zero;

		var i = 0;
		for (var j = len - i - 1; len - i >= 4; i += 4, j = len - i - 1)
		{
			var cV = Sse2.LoadVector128(&this.kMultFactorsPtr[j - 3]);
			cV = Sse2.Shuffle(cV, SO123);
			// Loads exactly the 4 bytes hashed: a 16 byte load would read past the end of the block.
			var sV = Sse41.ConvertToVector128Int32(buf + i);
			vPs = Sse2.Add(vPs, Sse41.MultiplyLow(cV, sV));
		}

		vPs = Sse2.Add(vPs, Sse2.Shuffle(vPs, S23_O1));
		vPs = Sse2.Add(vPs, Sse2.Shuffle(vPs, S1_O32));
		h += Sse2.ConvertToUInt32(vPs.AsUInt32());

		for (; i < len; i++)
		{
			var index = len - i - 1;
			ulong c = (uint)this.kMultFactors[index];
			h += c * buf[i];
		}

		return h & (K_BASE - 1);
	}
    /// <summary>
    ///     Generate a new hash from the bytes
    ///     The formula for calculating h is
    ///     h(0) = 1
    ///     h(n) = SUM {i=0}^{n-1} c^{n - i - 1} S[i]
    ///     where n is the length of S, and c is kMult.
    ///     In code,
    ///     h(n) = Sum(i: 0, n: len - 1, i => kMult ** (len - i - 1) span[i])
    ///     The final result is then MODded using binary and with kBase.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
	internal unsafe ulong Hash(byte* buf, int len)
	{
		if (len == 0) return 1;
		if (len == 1) return buf[0] * (uint)K_MULT;

		if (Avx2.IsSupported && len >= 8) return this.HashAvx2(buf, len);
		if (Sse41.IsSupported && len >= 4) return this.HashSse(buf, len);

		ulong h = 0;

		//// Old Version 
		//ulong hi = (span[0] * (uint)kMult) + span[1];

		//for (int j = 2; j < len; j++)
		//{
		//    hi = ((hi * kMult) + span[j]) & (kBase - 1);
		//}

		for (var i = 0; i < len; i++)
		{
			var index = len - i - 1;
			ulong c = (uint)this.kMultFactors[index];
			h += c * buf[i];
		}

		return h & (K_BASE - 1);
	}

    /// <summary>
    ///     Rolling update for the hash
    ///     First byte must be the first bytee that was used in the data
    ///     that was last encoded
    ///     new byte is the first byte position + Size
    /// </summary>
    /// <param name="oldHash">the original hash</param>
    /// <param name="firstByte">the original byte of the data for the first hash</param>
    /// <param name="newByte">the first byte of the new data to hash</param>
    /// <returns></returns>
    [SkipLocalsInit, MethodImpl(MethodImplOptions.AggressiveInlining)]
	internal ulong UpdateHash(ulong oldHash, byte firstByte, byte newByte)
	{
		// Remove the first byte from the hash
		var partial = (oldHash + this.removeTable[firstByte]) & (K_BASE - 1);

		// Do the hash step
		return (partial * K_MULT + newByte) & (K_BASE - 1);
	}

    /// <summary>
    ///     Dispose the rolling hash instance.
    ///     You must always dispose a manually created hashing instance, or memory leaks will occur.
    /// </summary>
    public void Dispose()
	{
		this.kMultFactorsHandle.Dispose();
	}
}