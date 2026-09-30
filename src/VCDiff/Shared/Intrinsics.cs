// Copyright (c) Metric and the Snowflake Authors.
// Licensed under the Apache License, Version 2.0.

using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics.X86;

namespace VCDiff.Shared;

internal static class Intrinsics
{
	public const int AVX_REGISTER_SIZE = 32;
	public const int SSE_REGISTER_SIZE = 16;
	public static readonly int MaxRegisterSize;
	public static readonly bool UseAvx;

	static Intrinsics()
	{
		if (Sse2.IsSupported)
		{
			MaxRegisterSize = SSE_REGISTER_SIZE;
			UseAvx = false;
		}

		if (Avx.IsSupported)
		{
			MaxRegisterSize = AVX_REGISTER_SIZE;
			UseAvx = true;
		}

		// Not set.
		if (MaxRegisterSize == 0)
		{
			// bytesLeft will never exceed.
			MaxRegisterSize = int.MaxValue;
		}
	}

	public static unsafe void FillArrayVectorized(long* first, int numValues, long value)
	{
		var bytesLeft = (long)numValues * sizeof(long);
		if (bytesLeft >= MaxRegisterSize)
		{
			// Note: This can be 0 cost in .NET 5 when paired with pinned GC.AllocateUnitializedArray.
			if (UseAvx)
				Avx2FillArray(first, value, ref bytesLeft);
			else
				Sse2FillArray(first, value, ref bytesLeft);

			// Fill rest of array.
			var elementsLeft = (int)(bytesLeft / sizeof(long));
			for (var x = numValues - elementsLeft; x < numValues; x++)
				first[x] = value;
		}
		else
		{
			// Copy remaining elements.
			for (var x = 0; x < numValues; x++)
				first[x] = value;
		}
	}

	[MethodImpl(MethodImplOptions.AggressiveOptimization)]
	private static unsafe void Sse2FillArray(long* first, long value, ref long bytesLeft)
	{
		// Initialize.
		var numValues = SSE_REGISTER_SIZE / sizeof(long);
		var vectorValues = stackalloc long[numValues];
		FillPointer(vectorValues, value, numValues);

		var vector = Sse2.LoadVector128(vectorValues);
		while (bytesLeft >= SSE_REGISTER_SIZE)
		{
			Sse2.Store(first, vector);
			first += numValues;
			bytesLeft -= SSE_REGISTER_SIZE;
		}
	}

	[MethodImpl(MethodImplOptions.AggressiveOptimization)]
	private static unsafe void Avx2FillArray(long* first, long value, ref long bytesLeft)
	{
		// Initialize.
		var numValues = AVX_REGISTER_SIZE / sizeof(long);
		var vectorValues = stackalloc long[numValues];
		FillPointer(vectorValues, value, numValues);

		var vector = Avx.LoadVector256(vectorValues);
		while (bytesLeft >= AVX_REGISTER_SIZE)
		{
			Avx.Store(first, vector);
			first += numValues;
			bytesLeft -= AVX_REGISTER_SIZE;
		}
	}

	private static unsafe void FillPointer(long* values, long value, int numValues)
	{
		for (var x = 0; x < numValues; x++)
		{
			*values = value;
			values += 1;
		}
	}
}