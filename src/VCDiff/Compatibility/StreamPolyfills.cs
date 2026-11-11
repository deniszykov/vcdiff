// Copyright (c) Denis Zykov and contributors (DeepSeek, ClaudeCode).
// Licensed under the Apache License, Version 2.0.

// Polyfills for the span/memory based Stream methods, which were introduced in
// netstandard2.1 (Stream.Read(Span<byte>), Stream.Write(ReadOnlySpan<byte>),
// Stream.ReadAsync(Memory<byte>), Stream.WriteAsync(ReadOnlyMemory<byte>)).
// They are declared in the System.IO namespace so that every caller that already
// imports System.IO picks them up without any per-call-site #if.

#if NETSTANDARD2_0
using System.Buffers;
using System.Threading;
using System.Threading.Tasks;

namespace System.IO
{
    internal static class StreamPolyfills
    {
        public static int Read(this Stream stream, Span<byte> buffer)
        {
            if (stream == null) throw new ArgumentNullException(nameof(stream));

            if (buffer.Length == 0) return 0;

            var rented = ArrayPool<byte>.Shared.Rent(buffer.Length);
            try
            {
                var read = stream.Read(rented, 0, buffer.Length);
                if (read > 0) rented.AsSpan(0, read).CopyTo(buffer);

                return read;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }

        public static void Write(this Stream stream, ReadOnlySpan<byte> buffer)
        {
            if (stream == null) throw new ArgumentNullException(nameof(stream));

            if (buffer.Length == 0) return;

            var rented = ArrayPool<byte>.Shared.Rent(buffer.Length);
            try
            {
                buffer.CopyTo(rented);
                stream.Write(rented, 0, buffer.Length);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }

        public static async Task<int> ReadAsync(this Stream stream, Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (stream == null) throw new ArgumentNullException(nameof(stream));

            if (buffer.Length == 0) return 0;

            var rented = ArrayPool<byte>.Shared.Rent(buffer.Length);
            try
            {
                var read = await stream.ReadAsync(rented, 0, buffer.Length, cancellationToken).ConfigureAwait(false);
                if (read > 0) rented.AsSpan(0, read).CopyTo(buffer.Span);

                return read;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }

        public static async Task WriteAsync(this Stream stream, ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (stream == null) throw new ArgumentNullException(nameof(stream));

            if (buffer.Length == 0) return;

            var rented = ArrayPool<byte>.Shared.Rent(buffer.Length);
            try
            {
                buffer.CopyTo(rented);
                await stream.WriteAsync(rented, 0, buffer.Length, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }
    }
}
#endif
