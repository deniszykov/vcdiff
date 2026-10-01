// Copyright (c) Denis Zykov and contributors (DeepSeek, ClaudeCode).
// Licensed under the Apache License, Version 2.0.

using System;
using System.Buffers;

namespace VCDiff.Shared;

/// <summary>
///     Random read access to the dictionary (source) data shared by the decoder's COPY-from-source path and the
///     encoder's block matching. The decoder calls <see cref="CopyTo" /> once per COPY instruction, never per byte;
///     the encoder's pointer-taking members compare dictionary bytes against the pinned target buffer in place.
/// </summary>
public unsafe interface ISourceReader : IDisposable
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

	/// <summary>
	///     Whether the <paramref name="length" /> bytes at <paramref name="offset" /> equal the bytes at
	///     <paramref name="other" />.
	/// </summary>
	bool SequenceEqual(long offset, byte* other, int length);

	/// <summary>
	///     Counts how many bytes starting at <paramref name="offset" /> equal the bytes starting at
	///     <paramref name="other" />, up to <paramref name="maxBytes" />.
	/// </summary>
	long MatchForward(long offset, byte* other, long maxBytes);

	/// <summary>
	///     Counts how many bytes before <paramref name="offset" /> equal the bytes before
	///     <paramref name="otherEnd" />, up to <paramref name="maxBytes" />.
	/// </summary>
	long MatchBackward(long offset, byte* otherEnd, long maxBytes);
	
	ReadOnlySequence<byte> Read(long offset, long bytesToRead);
}
