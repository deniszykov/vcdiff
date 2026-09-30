// Copyright (c) Denis Zykov and contributors (DeepSeek, ClaudeCode).
// Licensed under the Apache License, Version 2.0.

using System;
using System.Buffers;

namespace VCDiff.Shared;

/// <summary>
///     A growable, resumable byte buffer used to accumulate streaming input across
///     incremental transform calls. Bytes are appended at the end, read from a
///     logical position, and the consumed prefix can be compacted away.
/// </summary>
internal sealed class StreamingByteBuffer : IDisposable
{
	private readonly ArrayPool<byte> _pool;
	private byte[] _buffer;
	private int _capacity;
	private long _length;
	private long _position;

    /// <summary>
    ///     The number of bytes available to read from the current position.
    /// </summary>
    public long Available => this._length - this._position;

    /// <summary>
    ///     The unread bytes as a span.
    /// </summary>
    public ReadOnlySpan<byte> Remaining => this._buffer.AsSpan((int)this._position, (int)(this._length - this._position));

	public StreamingByteBuffer(int initialCapacity = 8192, ArrayPool<byte>? pool = null)
	{
		this._pool = pool ?? ArrayPool<byte>.Shared;
		this._capacity = Math.Max(1, initialCapacity);
		this._buffer = this._pool.Rent(this._capacity);
	}

    /// <summary>
    ///     Appends data to the end of the buffer.
    /// </summary>
    public void Append(ReadOnlySpan<byte> data)
	{
		if (data.Length == 0)
			return;

		this.EnsureCapacity(this._length + data.Length);
		data.CopyTo(this._buffer.AsSpan((int)this._length));
		this._length += data.Length;
	}

    /// <summary>
    ///     Advances the read position by <paramref name="count" /> bytes.
    /// </summary>
    public void Skip(int count)
	{
		this._position += count;
	}

    /// <summary>
    ///     Discards the consumed prefix so the buffer does not grow unboundedly.
    /// </summary>
    public void Compact()
	{
		if (this._position <= 0)
			return;

		var remaining = this._length - this._position;
		if (remaining > 0) this._buffer.AsSpan((int)this._position, (int)remaining).CopyTo(this._buffer.AsSpan(0, (int)remaining));

		this._length = remaining;
		this._position = 0;
	}

	private void EnsureCapacity(long required)
	{
		if (required <= this._capacity)
			return;

		var newCapacity = this._capacity;
		while (newCapacity < required && newCapacity < int.MaxValue / 2)
		{
			newCapacity *= 2;
		}

		if (newCapacity < required)
			newCapacity = (int)Math.Min(required, int.MaxValue);

		var newBuffer = this._pool.Rent(newCapacity);
		this._buffer.AsSpan(0, (int)this._length).CopyTo(newBuffer.AsSpan());
		this._pool.Return(this._buffer, false);
		this._buffer = newBuffer;
		this._capacity = newCapacity;
	}

	public void Dispose()
	{
		this._pool.Return(this._buffer, false);
		this._buffer = Array.Empty<byte>();
	}
}