// Copyright (c) Denis Zykov and contributors (DeepSeek, ClaudeCode).
// Licensed under the Apache License, Version 2.0.

using System;
using System.Buffers;

namespace VCDiff.Shared
{
    /// <summary>
    /// A growable, resumable byte buffer used to accumulate streaming input across
    /// incremental transform calls. Bytes are appended at the end, read from a
    /// logical position, and the consumed prefix can be compacted away.
    /// </summary>
    internal sealed class StreamingByteBuffer : IDisposable
    {
        private byte[] _buffer;
        private int _capacity;
        private long _length;
        private long _position;
        private readonly ArrayPool<byte> _pool;

        public StreamingByteBuffer(int initialCapacity = 8192, ArrayPool<byte>? pool = null)
        {
            _pool = pool ?? ArrayPool<byte>.Shared;
            _capacity = Math.Max(1, initialCapacity);
            _buffer = _pool.Rent(_capacity);
        }

        /// <summary>
        /// The number of bytes available to read from the current position.
        /// </summary>
        public long Available => _length - _position;

        /// <summary>
        /// The unread bytes as a span.
        /// </summary>
        public ReadOnlySpan<byte> Remaining => _buffer.AsSpan((int)_position, (int)(_length - _position));

        /// <summary>
        /// Appends data to the end of the buffer.
        /// </summary>
        public void Append(ReadOnlySpan<byte> data)
        {
            if (data.Length == 0)
                return;

            EnsureCapacity(_length + data.Length);
            data.CopyTo(_buffer.AsSpan((int)_length));
            _length += data.Length;
        }

        /// <summary>
        /// Advances the read position by <paramref name="count"/> bytes.
        /// </summary>
        public void Skip(int count) => _position += count;

        /// <summary>
        /// Discards the consumed prefix so the buffer does not grow unboundedly.
        /// </summary>
        public void Compact()
        {
            if (_position <= 0)
                return;

            long remaining = _length - _position;
            if (remaining > 0)
                _buffer.AsSpan((int)_position, (int)remaining).CopyTo(_buffer.AsSpan(0, (int)remaining));

            _length = remaining;
            _position = 0;
        }

        private void EnsureCapacity(long required)
        {
            if (required <= _capacity)
                return;

            int newCapacity = _capacity;
            while (newCapacity < required && newCapacity < int.MaxValue / 2)
                newCapacity *= 2;

            if (newCapacity < required)
                newCapacity = (int)Math.Min(required, int.MaxValue);

            var newBuffer = _pool.Rent(newCapacity);
            _buffer.AsSpan(0, (int)_length).CopyTo(newBuffer.AsSpan());
            _pool.Return(_buffer, false);
            _buffer = newBuffer;
            _capacity = newCapacity;
        }

        public void Dispose()
        {
            _pool.Return(_buffer, false);
            _buffer = Array.Empty<byte>();
        }
    }
}
