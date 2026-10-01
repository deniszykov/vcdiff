// Copyright (c) Denis Zykov and contributors (DeepSeek, ClaudeCode).
// Licensed under the Apache License, Version 2.0.

using System;
using System.Buffers;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace VCDiff.Shared;

/// <summary>
///     Random access over a dictionary that is stored as one or more memory segments. The segments are
///     referenced in place and never copied or pinned: each read is served as a <see cref="Span{T}" /> over the
///     original segment, so the dictionary does not have to be available as a single contiguous block of memory.
/// </summary>
public sealed unsafe class SequenceSourceReader : ISourceReader
{
	private readonly ReadOnlySequence<byte> sequence;
	private readonly ReadOnlyMemory<byte>[] segments;
	private Action? releaseSequence;
	private bool disposed;

	// When every segment but the last has the same power of two size, the segment index is offset >> shift.
	private readonly int shift;

	// Start offset of every segment, followed by the total length.
	private readonly long[] starts;
	private int lastSegment;

	public long Length
	{
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		get => this.starts[this.starts.Length - 1];
	}

	/// <summary>
	///     Indexes the segments of <paramref name="sequence" />. The memory behind the sequence must stay
	///     alive and unchanged until this instance is disposed; it is referenced, not copied or pinned.
	/// </summary>
	/// <param name="sequence">The dictionary data to index.</param>
	/// <param name="releaseSequence">
	///     Optional action invoked exactly once when this instance is disposed, allowing an external resource's
	///     lifetime to be tied to this reader (for example <c>stream.Dispose</c>).
	/// </param>
	public SequenceSourceReader(ReadOnlySequence<byte> sequence, Action? releaseSequence = null)
	{
		if (sequence.Length > int.MaxValue)
			throw VcdiffException.DictionaryTooLarge();

		this.sequence = sequence;
		this.releaseSequence = releaseSequence;

		var count = 0;
		foreach (var memory in sequence)
		{
			if (!memory.IsEmpty)
				count++;
		}

		this.segments = new ReadOnlyMemory<byte>[count];
		this.starts = new long[count + 1];

		var index = 0;
		long offset = 0;
		foreach (var memory in sequence)
		{
			if (memory.IsEmpty)
				continue;

			this.segments[index] = memory;
			this.starts[index] = offset;
			offset += memory.Length;
			index++;
		}

		this.starts[count] = offset;
		this.shift = this.CalcShift();
	}

	private int CalcShift()
	{
		var count = this.segments.Length;
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
		var hi = this.segments.Length - 1;
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
	///     Gets the span of the segment containing <paramref name="offset" />, starting at that offset and
	///     extending to the end of the segment. <paramref name="offset" /> must be less than <see cref="Length" />.
	/// </summary>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private ReadOnlySpan<byte> GetSpan(long offset)
	{
		var i = this.FindSegment(offset);
		return this.segments[i].Span.Slice((int)(offset - this.starts[i]));
	}

	/// <summary>
	///     Gets the span of the segment containing <paramref name="offset" /> - 1, from the start of the
	///     segment up to (but not including) <paramref name="offset" />. <paramref name="offset" /> must be
	///     greater than zero.
	/// </summary>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private ReadOnlySpan<byte> GetSpanBefore(long offset)
	{
		var i = this.FindSegment(offset - 1);
		return this.segments[i].Span.Slice(0, (int)(offset - this.starts[i]));
	}

	/// <summary>
	///     Copies <c>destination.Length</c> bytes starting at <paramref name="offset" />.
	/// </summary>
	public void CopyTo(long offset, Span<byte> destination)
	{
		this.sequence.Slice(offset, destination.Length).CopyTo(destination);
	}

	/// <summary>
	///     Whether the <paramref name="length" /> bytes at <paramref name="offset" /> equal the bytes at
	///     <paramref name="other" />.
	/// </summary>
	public bool SequenceEqual(long offset, byte* other, int length)
	{
		if (offset < 0 || length < 0 || offset + length > this.Length)
			throw new ArgumentOutOfRangeException(nameof(offset));

		return this.MatchForward(offset, other, length) == length;
	}

	/// <summary>
	///     Counts how many bytes starting at <paramref name="offset" /> equal the bytes starting at
	///     <paramref name="other" />, up to <paramref name="maxBytes" />.
	/// </summary>
	public long MatchForward(long offset, byte* other, long maxBytes)
	{
		if (maxBytes < 0)
			throw new ArgumentOutOfRangeException(nameof(maxBytes));
		if (offset < 0 || offset > this.Length)
			throw new ArgumentOutOfRangeException(nameof(offset));

		var remaining = Math.Min(maxBytes, this.Length - offset);
		var otherSpan = new ReadOnlySpan<byte>(other, (int)remaining);
		long found = 0;
		while (found < remaining)
		{
			var span = this.GetSpan(offset + found);
			var take = (int)Math.Min(span.Length, remaining - found);
			var matched = ByteComparer.CommonPrefixLength(span.Slice(0, take), otherSpan.Slice((int)found, take));
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
		if (maxBytes < 0)
			throw new ArgumentOutOfRangeException(nameof(maxBytes));
		if (offset < 0 || offset > this.Length)
			throw new ArgumentOutOfRangeException(nameof(offset));

		var remaining = Math.Min(maxBytes, offset);
		var otherSpan = new ReadOnlySpan<byte>(otherEnd - remaining, (int)remaining);
		long found = 0;
		while (found < remaining)
		{
			var span = this.GetSpanBefore(offset - found);
			var take = (int)Math.Min(span.Length, remaining - found);
			var matched = ByteComparer.CommonSuffixLength(
				span.Slice(span.Length - take, take),
				otherSpan.Slice((int)(remaining - found - take), take));
			found += matched;
			if (matched < take)
				break;
		}

		return found;
	}

	/// <inheritdoc />
	public ReadOnlySequence<byte> Read(long offset, long bytesToRead)
	{
		return this.sequence.Slice(offset, bytesToRead);
	}

	/// <summary>
	///     Invokes the optional release action once. The sequence itself is referenced, not pinned, so it is kept
	///     alive by this instance and released when the caller drops the last reference.
	/// </summary>
	public void Dispose()
	{
		if (this.disposed)
			return;

		this.disposed = true;
		this.releaseSequence?.Invoke();
		this.releaseSequence = null;
	}
}
