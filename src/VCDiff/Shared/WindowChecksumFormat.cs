// Copyright (c) Metric and the Snowflake Authors.
// Licensed under the Apache License, Version 2.0.

namespace VCDiff.Shared;

/// <summary>
///     Which checksum format to output.
/// </summary>
public enum WindowChecksumFormat
{
    /// <summary>
    ///     Do not emit a checksum.
    /// </summary>
    None,
    /// <summary>
    ///     Emit a Google compatible SDCH checksum.
    /// </summary>
    Sdch,
    /// <summary>
    ///     Emit an Xdelta3 checksum.
    /// </summary>
    Xdelta3
}