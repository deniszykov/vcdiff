// Copyright (c) Denis Zykov and contributors (DeepSeek, ClaudeCode).
// Licensed under the Apache License, Version 2.0.

namespace VCDiff.Shared;

/// <summary>
///     The outcome of parsing a sub-component of a delta (a varint, an address or an instruction): the whole value
///     was read, more input is needed, or the data is invalid.
/// </summary>
internal enum ParseResult
{
	/// <summary>The value was read in full.</summary>
	Success,

	/// <summary>More input is needed to read the whole value.</summary>
	NeedMoreData,

	/// <summary>The data is malformed or invalid.</summary>
	Error
}
