// Copyright (c) Denis Zykov and contributors (DeepSeek, ClaudeCode).
// Licensed under the Apache License, Version 2.0.

// Polyfills for the span-based IncrementalHash methods (AppendData(ReadOnlySpan<byte>)
// and TryGetHashAndReset(Span<byte>, out int)), which on netstandard2.0 may only be
// available through the byte[] overloads. They are declared in the
// System.Security.Cryptography namespace so callers that already import that namespace
// pick them up without any per-call-site #if.

#if NETSTANDARD2_0
using System.Buffers;

namespace System.Security.Cryptography
{
    internal static class IncrementalHashPolyfills
    {
        public static void AppendData(this IncrementalHash hash, ReadOnlySpan<byte> data)
        {
            if (hash == null) throw new ArgumentNullException(nameof(hash));

            if (data.Length == 0) return;

            var rented = ArrayPool<byte>.Shared.Rent(data.Length);
            try
            {
                data.CopyTo(rented);
                hash.AppendData(rented, 0, data.Length);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }

        public static bool TryGetHashAndReset(this IncrementalHash hash, Span<byte> destination, out int bytesWritten)
        {
            if (hash == null) throw new ArgumentNullException(nameof(hash));

            var result = hash.GetHashAndReset();
            bytesWritten = Math.Min(result.Length, destination.Length);
            result.AsSpan(0, bytesWritten).CopyTo(destination);
            return result.Length <= destination.Length;
        }
    }
}
#endif
