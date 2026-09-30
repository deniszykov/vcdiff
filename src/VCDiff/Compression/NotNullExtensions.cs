using System;

namespace VCDiff.Compression
{
    internal static class NotNullExtensions
    {
        public static T NotNull<T>(this T? obj, string? message = null)
            where T : class
        {
            if (obj is null)
            {
                throw new InvalidOperationException(message ?? "Value is null");
            }

            return obj;
        }
    }
}
