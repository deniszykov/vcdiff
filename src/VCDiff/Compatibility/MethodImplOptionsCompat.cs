// Copyright (c) Denis Zykov and contributors (DeepSeek, ClaudeCode).
// Licensed under the Apache License, Version 2.0.

using System.Runtime.CompilerServices;

namespace System.Runtime.CompilerServices
{
    /// <summary>
    ///     Re-declares the <c>MethodImplOptions.AggressiveOptimization</c> JIT hint (value 0x200), which exists only on
    ///     .NET Core 3.0+ and is not defined on netstandard2.0. On targets that define the enum member the two values
    ///     are identical, so the attribute can be used uniformly on every target framework without per-call-site
    ///     preprocessor guards.
    /// </summary>
    internal static class MethodImplOptionsCompat
    {
        public const MethodImplOptions AggressiveOptimization = (MethodImplOptions)0x200;
    }
}
