using System;
using System.Runtime.CompilerServices;

namespace VCDiff.Compression
{
    internal static class ThrowHelper
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void ThrowIfNegativeOrZero(int value, string? paramName = null)
        {
            if (value <= 0)
            {
                throw new ArgumentOutOfRangeException(paramName);
            }
        }
    }
}
