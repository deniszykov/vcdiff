// Copyright (c) Metric and the Snowflake Authors.
// Licensed under the Apache License, Version 2.0.

namespace VCDiff;

/// <summary>
///     The result of a VCDIFF encode or decode operation.
/// </summary>
public enum VcdiffResult
{
	/// <summary>
	///     The whole delta was encoded or applied successfully.
	/// </summary>
	Success = 0,

	/// <summary>
	///     The operation failed because the input was invalid or inconsistent.
	/// </summary>
	Error = -1,

	/// <summary>
	///     The input ended before a complete delta was available (or nothing was left to decode).
	/// </summary>
	Incomplete = -2
}
