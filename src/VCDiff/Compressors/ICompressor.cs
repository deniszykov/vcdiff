// Copyright (c) Metric and the Snowflake Authors.
// Licensed under the Apache License, Version 2.0.

using VCDiff.Includes;
using VCDiff.Shared;

namespace VCDiff.Compressors;

/// <summary>
///     A compression method for secondary compression, for use when <see cref="VcDiffCodeFlags.VCDDECOMPRESS" /> is
///     enabled in the header and the appropriate <see cref="VcDiffCompressFlags" /> flag is enabled for the section in the
///     window.
/// </summary>
/// <remarks>
///     Implementations are stateful, and a single instance must be used for the entire file for a single operation
///     (compression or decompression).
/// </remarks>
internal interface ICompressor
{
	PinnedArrayRental Decompress(WindowSectionType windowSectionType, PinnedArrayRental sectionData);
}