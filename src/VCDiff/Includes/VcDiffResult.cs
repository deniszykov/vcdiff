// Copyright (c) Metric and the Snowflake Authors.
// Licensed under the Apache License, Version 2.0.

namespace VCDiff.Includes;

/// <summary>
///     The result of a VCDIFF Operation.
/// </summary>
public enum VcDiffResult
{
    /// <summary>
    ///     The diff operation was successful.
    /// </summary>
    SUCCESS = 0,
    /// <summary>
    ///     An error occurred during the diff operation.
    /// </summary>
    ERROR = -1,
    /// <summary>
    ///     End of stream encountered.
    /// </summary>
    EOD = -2
}
