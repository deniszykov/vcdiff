// Copyright (c) Denis Zykov and contributors (DeepSeek, ClaudeCode).
// Licensed under the Apache License, Version 2.0.

// Polyfill for [SkipLocalsInit], which was introduced in .NET 5.0.
// The C# compiler recognizes this attribute by its full name
// (System.Runtime.CompilerServices.SkipLocalsInitAttribute) and, when present,
// skips zero-initialization of locals in the attributed method. Defining it
// here lets the netcoreapp3.1 target use the attribute and keep the same
// skip-init code generation as the newer targets.
#if !NET5_0_OR_GREATER
namespace System.Runtime.CompilerServices
{
    [System.AttributeUsage(
        System.AttributeTargets.Method |
        System.AttributeTargets.Property |
        System.AttributeTargets.Constructor |
        System.AttributeTargets.Class |
        System.AttributeTargets.Struct |
        System.AttributeTargets.Module |
        System.AttributeTargets.Interface,
        Inherited = false)]
    internal sealed class SkipLocalsInitAttribute : System.Attribute
    {
    }
}
#endif
