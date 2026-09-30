// Copyright (c) Denis Zykov and contributors (DeepSeek, ClaudeCode).
// Licensed under the Apache License, Version 2.0.

using System;

namespace VCDiff.Compression;

internal static class NotNullExtensions
{
	public static T NotNull<T>(this T? obj, string? message = null)
		where T : class
	{
		if (obj is null) throw new InvalidOperationException(message ?? "Value is null");

		return obj;
	}
}