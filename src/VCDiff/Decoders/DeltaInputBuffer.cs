// Copyright (c) Denis Zykov and contributors (DeepSeek, ClaudeCode).
// Licensed under the Apache License, Version 2.0.

using System;
using System.Buffers;

namespace VCDiff.Decoders;

/// <summary>
///     The FIFO buffer that holds the delta bytes <see cref="VcDiffDecoder" /> has taken from the caller but
///     not parsed yet, across incremental decode calls. Bytes are appended at the end, read from a logical position,
///     and the consumed prefix can be compacted away.
/// </summary>
/// <remarks>
///     Compaction is amortized: the unread bytes are only moved to the front when the consumed
///     prefix is at least as large as them (or when an append would otherwise have to grow the
///     buffer), so feeding input one byte at a time stays linear. The buffer grows by doubling and
///     every rented array is returned to the pool.
/// </remarks>
internal sealed class DeltaInputBuffer : IDisposable
{
	private readonly ArrayPool<byte> _pool;
	private byte[] _buffer;
	private int _length;
	private int _position;

	public DeltaInputBuffer(int initialCapacity, ArrayPool<byte> pool)
	{
		this._pool = pool;
		this._buffer = pool.Rent(Math.Max(1, initialCapacity));
	}

    /// <summary>
    ///     The number of bytes available to read from the current position.
    /// </summary>
    public int Available => this._length - this._position;

    /// <summary>
    ///     The unread bytes as a span.
    /// </summary>
    public ReadOnlySpan<byte> Remaining => this._buffer.AsSpan(this._position, this._length - this._position);

    /// <summary>
    ///     Returns a single-segment <see cref="ReadOnlySequence{T}" /> over the unread bytes, starting
    ///     <paramref name="offset" /> bytes past the current read position. The sequence is only valid
    ///     until the next <see cref="Append" />, <see cref="Compact" /> or <see cref="Dispose" />.
    /// </summary>
    public ReadOnlySequence<byte> GetReadOnlySequence(int offset, int length)
	{
		if (offset < 0 || length < 0 || offset + length > this._length - this._position)
			throw new ArgumentOutOfRangeException(nameof(length));

		return new ReadOnlySequence<byte>(this._buffer, this._position + offset, length);
	}

    /// <summary>
    ///     Appends data to the end of the buffer.
    /// </summary>
    public void Append(ReadOnlySpan<byte> data)
	{
		if (data.Length == 0)
			return;

		this.EnsureCapacity(data.Length);
		data.CopyTo(this._buffer.AsSpan(this._length));
		this._length += data.Length;
	}

    /// <summary>
    ///     Advances the read position by <paramref name="count" /> bytes.
    /// </summary>
    public void Skip(int count)
	{
		if (count < 0 || count > this._length - this._position)
			throw new ArgumentOutOfRangeException(nameof(count));

		this._position += count;
	}

    /// <summary>
    ///     Discards the consumed prefix so the buffer does not grow unboundedly. The unread bytes are
    ///     only moved when that is cheap relative to the bytes already consumed.
    /// </summary>
    public void Compact()
	{
		if (this._position == 0)
			return;

		var remaining = this._length - this._position;
		if (remaining == 0)
		{
			this._length = 0;
			this._position = 0;
			return;
		}

		// Moving more bytes than were consumed since the last move would make tiny appends quadratic.
		if (remaining > this._position)
			return;

		this.MoveToFront();
	}

	private void MoveToFront()
	{
		var remaining = this._length - this._position;
		if (remaining > 0)
			this._buffer.AsSpan(this._position, remaining).CopyTo(this._buffer.AsSpan(0, remaining));

		this._length = remaining;
		this._position = 0;
	}

	private void EnsureCapacity(int extra)
	{
		if (this._buffer.Length - this._length >= extra)
			return;

		var remaining = this._length - this._position;
		var required = (long)remaining + extra;
		if (required > int.MaxValue)
			throw VcdiffException.StreamingBufferTooLarge();

		// Reclaim the consumed prefix before growing; this is amortized by the growth itself.
		if (required <= this._buffer.Length)
		{
			this.MoveToFront();
			return;
		}

		var newCapacity = (long)Math.Max(1, this._buffer.Length);
		while (newCapacity < required)
		{
			newCapacity *= 2;
		}

		var newBuffer = this._pool.Rent((int)Math.Min(newCapacity, int.MaxValue));
		this._buffer.AsSpan(this._position, remaining).CopyTo(newBuffer);
		this._pool.Return(this._buffer, false);
		this._buffer = newBuffer;
		this._length = remaining;
		this._position = 0;
	}

	public void Dispose()
	{
		if (this._buffer.Length == 0)
			return;

		this._pool.Return(this._buffer, false);
		this._buffer = Array.Empty<byte>();
		this._length = 0;
		this._position = 0;
	}
}
