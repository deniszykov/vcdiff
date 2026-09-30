using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace VCDiff.Shared
{
    internal static class Extensions
    {
        public static Span<byte> AsSpanFast(this byte[] data)
        {
            return MemoryMarshal.CreateSpan(ref MemoryMarshal.GetReference(data.AsSpan()), data.Length);
        }

        public static Span<byte> AsSpanFast(this byte[] data, int length)
        {
            return MemoryMarshal.CreateSpan(ref MemoryMarshal.GetReference(data.AsSpan()), length);
        }

    }
}
