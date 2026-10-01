// Copyright (c) Denis Zykov and contributors (DeepSeek, ClaudeCode).
// Licensed under the Apache License, Version 2.0.

using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace VCDiff.Shared;

/// <summary>
///     SIMD-accelerated byte comparison over spans, shared by the in-memory and stream-backed dictionary
///     readers. It works directly on spans through <see cref="MemoryMarshal.GetReference{T}(ReadOnlySpan{T})" />
///     and <see cref="Unsafe" />, so the callers never need to pin or take raw pointers to their buffers.
/// </summary>
internal static class ByteComparer
{
	/// <summary>
	///     Counts how many bytes at the start of <paramref name="a" /> and <paramref name="b" /> are equal, up to
	///     the shorter length.
	/// </summary>
	public static long CommonPrefixLength(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
	{
		var length = Math.Min(a.Length, b.Length);
		ref var aRef = ref MemoryMarshal.GetReference(a);
		ref var bRef = ref MemoryMarshal.GetReference(b);
		long i = 0;

		if (Vector.IsHardwareAccelerated)
		{
			var vectorSize = Vector<byte>.Count;
			while (i <= length - vectorSize)
			{
				if (Unsafe.ReadUnaligned<Vector<byte>>(ref Unsafe.AddByteOffset(ref aRef, (nint)i)) !=
					Unsafe.ReadUnaligned<Vector<byte>>(ref Unsafe.AddByteOffset(ref bRef, (nint)i)))
					break;

				i += vectorSize;
			}
		}

		while (i <= length - sizeof(ulong) &&
			Unsafe.ReadUnaligned<ulong>(ref Unsafe.AddByteOffset(ref aRef, (nint)i)) ==
			Unsafe.ReadUnaligned<ulong>(ref Unsafe.AddByteOffset(ref bRef, (nint)i)))
		{
			i += sizeof(ulong);
		}

		while (i < length && Unsafe.AddByteOffset(ref aRef, (nint)i) == Unsafe.AddByteOffset(ref bRef, (nint)i))
		{
			i++;
		}

		return i;
	}

	/// <summary>
	///     Counts how many bytes at the end of <paramref name="a" /> and <paramref name="b" /> are equal, up to
	///     the shorter length.
	/// </summary>
	public static long CommonSuffixLength(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
	{
		var length = Math.Min(a.Length, b.Length);
		ref var aRef = ref MemoryMarshal.GetReference(a);
		ref var bRef = ref MemoryMarshal.GetReference(b);
		long i = 0;

		if (Vector.IsHardwareAccelerated)
		{
			var vectorSize = Vector<byte>.Count;
			while (i <= length - vectorSize)
			{
				if (Unsafe.ReadUnaligned<Vector<byte>>(ref Unsafe.AddByteOffset(ref aRef, (nint)(a.Length - i - vectorSize))) !=
					Unsafe.ReadUnaligned<Vector<byte>>(ref Unsafe.AddByteOffset(ref bRef, (nint)(b.Length - i - vectorSize))))
					break;

				i += vectorSize;
			}
		}

		while (i <= length - sizeof(ulong) &&
			Unsafe.ReadUnaligned<ulong>(ref Unsafe.AddByteOffset(ref aRef, (nint)(a.Length - i - sizeof(ulong)))) ==
			Unsafe.ReadUnaligned<ulong>(ref Unsafe.AddByteOffset(ref bRef, (nint)(b.Length - i - sizeof(ulong)))))
		{
			i += sizeof(ulong);
		}

		while (i < length &&
			Unsafe.AddByteOffset(ref aRef, (nint)(a.Length - i - 1)) ==
			Unsafe.AddByteOffset(ref bRef, (nint)(b.Length - i - 1)))
		{
			i++;
		}

		return i;
	}
}
