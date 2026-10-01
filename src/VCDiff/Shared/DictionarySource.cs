// Copyright (c) Denis Zykov and contributors (DeepSeek, ClaudeCode).
// Licensed under the Apache License, Version 2.0.

using System;
using System.Buffers;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace VCDiff.Shared;

/// <summary>
///     Random access over a dictionary that is stored as one or more memory segments.
///     The segments are pinned in place and never copied, so the dictionary does not have to be
///     available as a single contiguous block of memory.
/// </summary>
internal sealed unsafe class DictionarySource : IDictionaryReader
{
	private readonly MemoryHandle[] handles;
	private readonly byte*[] pointers;

	// When every segment but the last has the same power of two size, the segment index is offset >> shift.
	private readonly int shift;

	// Start offset of every segment, followed by the total length.
	private readonly long[] starts;
	private bool disposed;
	private int lastSegment;

	public long Length
	{
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		get => this.starts[this.starts.Length - 1];
	}

    /// <summary>
    ///     Pins the segments of <paramref name="sequence" />. The memory behind the sequence must stay
    ///     alive and unchanged until this instance is disposed.
    /// </summary>
    public DictionarySource(ReadOnlySequence<byte> sequence)
	{
		if (sequence.Length > int.MaxValue)
			throw new ArgumentException("The dictionary can not be larger than 2 GiB.", nameof(sequence));

		var count = 0;
		foreach (var memory in sequence)
		{
			if (!memory.IsEmpty)
				count++;
		}

		this.pointers = new byte*[count];
		this.starts = new long[count + 1];
		this.handles = new MemoryHandle[count];

		var index = 0;
		long offset = 0;
		try
		{
			foreach (var memory in sequence)
			{
				if (memory.IsEmpty)
					continue;

				this.handles[index] = memory.Pin();
				this.pointers[index] = (byte*)this.handles[index].Pointer;
				this.starts[index] = offset;
				offset += memory.Length;
				index++;
			}
		}
		catch
		{
			this.Dispose();
			throw;
		}

		this.starts[count] = offset;
		this.shift = this.CalcShift();
	}

	~DictionarySource()
	{
		this.Dispose();
	}

	private int CalcShift()
	{
		var count = this.pointers.Length;
		if (count <= 1)
			return 63;

		var size = this.starts[1];
		if ((size & (size - 1)) != 0)
			return -1;

		for (var i = 1; i < count - 1; i++)
		{
			if (this.starts[i + 1] - this.starts[i] != size)
				return -1;
		}

		if (this.starts[count] - this.starts[count - 1] > size)
			return -1;

		return BitOperations.Log2((ulong)size);
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private int FindSegment(long offset)
	{
		if (this.shift >= 0)
			return (int)(offset >> this.shift);

		return this.FindSegmentSlow(offset);
	}

	private int FindSegmentSlow(long offset)
	{
		var i = this.lastSegment;
		if (offset >= this.starts[i] && offset < this.starts[i + 1])
			return i;

		var lo = 0;
		var hi = this.pointers.Length - 1;
		while (lo < hi)
		{
			var mid = (lo + hi + 1) >> 1;
			if (this.starts[mid] <= offset)
				lo = mid;
			else
				hi = mid - 1;
		}

		this.lastSegment = lo;
		return lo;
	}

    /// <summary>
    ///     Gets a pointer to the byte at <paramref name="offset" /> and the number of bytes that can
    ///     be read from it before the end of the segment. <paramref name="offset" /> must be less
    ///     than <see cref="Length" />.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
	public byte* GetPointer(long offset, out long available)
	{
		var i = this.FindSegment(offset);
		available = this.starts[i + 1] - offset;
		return this.pointers[i] + (offset - this.starts[i]);
	}

    /// <summary>
    ///     Gets a pointer one past the byte at <paramref name="offset" /> - 1 and the number of bytes
    ///     that can be read before it down to the start of the segment. <paramref name="offset" />
    ///     must be greater than zero.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
	private byte* GetPointerBefore(long offset, out long available)
	{
		var i = this.FindSegment(offset - 1);
		available = offset - this.starts[i];
		return this.pointers[i] + available;
	}

    /// <summary>
    ///     Copies <c>destination.Length</c> bytes starting at <paramref name="offset" />.
    /// </summary>
    public void CopyTo(long offset, Span<byte> destination)
	{
		while (destination.Length > 0)
		{
			var p = this.GetPointer(offset, out var available);
			var take = (int)Math.Min(available, destination.Length);
			new ReadOnlySpan<byte>(p, take).CopyTo(destination);
			destination = destination.Slice(take);
			offset += take;
		}
	}

    /// <summary>
    ///     Whether the <paramref name="length" /> bytes at <paramref name="offset" /> equal the bytes at
    ///     <paramref name="other" />.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
	public bool SequenceEqual(long offset, byte* other, int length)
	{
		var p = this.GetPointer(offset, out var available);
		if (available >= length)
			return new ReadOnlySpan<byte>(p, length).SequenceEqual(new ReadOnlySpan<byte>(other, length));

		return this.MatchForward(offset, other, length) == length;
	}

    /// <summary>
    ///     Counts how many bytes starting at <paramref name="offset" /> equal the bytes starting at
    ///     <paramref name="other" />, up to <paramref name="maxBytes" />.
    /// </summary>
    public long MatchForward(long offset, byte* other, long maxBytes)
	{
		long found = 0;
		while (found < maxBytes)
		{
			var p = this.GetPointer(offset + found, out var available);
			var take = Math.Min(available, maxBytes - found);
			var matched = CommonPrefixLength(p, other + found, take);
			found += matched;
			if (matched < take)
				break;
		}

		return found;
	}

    /// <summary>
    ///     Counts how many bytes before <paramref name="offset" /> equal the bytes before
    ///     <paramref name="otherEnd" />, up to <paramref name="maxBytes" />.
    /// </summary>
    public long MatchBackward(long offset, byte* otherEnd, long maxBytes)
	{
		long found = 0;
		while (found < maxBytes)
		{
			var p = this.GetPointerBefore(offset - found, out var available);
			var take = Math.Min(available, maxBytes - found);
			var matched = CommonSuffixLength(p, otherEnd - found, take);
			found += matched;
			if (matched < take)
				break;
		}

		return found;
	}

	private static long CommonPrefixLength(byte* a, byte* b, long length)
	{
		long i = 0;
		if (Vector.IsHardwareAccelerated)
		{
			var vectorSize = Vector<byte>.Count;
			while (i <= length - vectorSize)
			{
				if (Unsafe.ReadUnaligned<Vector<byte>>(a + i) != Unsafe.ReadUnaligned<Vector<byte>>(b + i))
					break;

				i += vectorSize;
			}
		}

		while (i <= length - sizeof(ulong) && Unsafe.ReadUnaligned<ulong>(a + i) == Unsafe.ReadUnaligned<ulong>(b + i))
		{
			i += sizeof(ulong);
		}

		while (i < length && a[i] == b[i])
		{
			i++;
		}

		return i;
	}

	private static long CommonSuffixLength(byte* aEnd, byte* bEnd, long length)
	{
		long i = 0;
		if (Vector.IsHardwareAccelerated)
		{
			var vectorSize = Vector<byte>.Count;
			while (i <= length - vectorSize)
			{
				if (Unsafe.ReadUnaligned<Vector<byte>>(aEnd - i - vectorSize) != Unsafe.ReadUnaligned<Vector<byte>>(bEnd - i - vectorSize))
					break;

				i += vectorSize;
			}
		}

		while (i <= length - sizeof(ulong) && Unsafe.ReadUnaligned<ulong>(aEnd - i - sizeof(ulong)) == Unsafe.ReadUnaligned<ulong>(bEnd - i - sizeof(ulong)))
		{
			i += sizeof(ulong);
		}

		while (i < length && aEnd[-i - 1] == bEnd[-i - 1])
		{
			i++;
		}

		return i;
	}

	public void Dispose()
	{
		if (this.disposed)
			return;

		this.disposed = true;
		for (var i = 0; i < this.handles.Length; i++) this.handles[i].Dispose();

		GC.SuppressFinalize(this);
	}
}