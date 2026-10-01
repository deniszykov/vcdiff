// Copyright (c) Denis Zykov and contributors (DeepSeek, ClaudeCode).
// Licensed under the Apache License, Version 2.0.

using System;

namespace VCDiff.Shared;

/// <summary>
///     Random read access to the dictionary (source) data used by the decoder's COPY-from-source path.
///     It is called once per COPY instruction, never per byte.
/// </summary>
internal interface IDictionaryReader : IDisposable
{
	/// <summary>
	///     The dictionary length in bytes.
	/// </summary>
	long Length { get; }

	/// <summary>
	///     Copies <c>destination.Length</c> bytes starting at <paramref name="offset" />. The range must lie within
	///     <see cref="Length" />.
	/// </summary>
	void CopyTo(long offset, Span<byte> destination);
}
