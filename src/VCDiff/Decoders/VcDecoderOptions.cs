// Copyright (c) Denis Zykov and contributors (DeepSeek, ClaudeCode).
// Licensed under the Apache License, Version 2.0.

using System.Buffers;
using Microsoft.IO;
using VCDiff.Shared;

namespace VCDiff.Decoders;

/// <summary>
///     Options for configuring a <see cref="VcDecoder" />.
/// </summary>
public class VcDecoderOptions
{
	internal const int DEFAULT_MAX_TARGET_FILE_SIZE = 67108864; // 64 MB

    /// <summary>
    ///     The maximum target file size (and target window size) in bytes.
    /// </summary>
    public int MaxTargetFileSize { get; set; } = DEFAULT_MAX_TARGET_FILE_SIZE;

    /// <summary>
    ///     Whether to disable checksums when applying the delta. This can be dangerous, but can
    ///     be useful when the input file differs in ways that the delta does not reference.
    /// </summary>
    public bool DisableChecksums { get; set; }

    /// <summary>
    ///     The <see cref="ArrayPool{T}" /> used to rent internal byte buffers. When
    ///     <see langword="null" />, <see cref="ArrayPool{T}.Shared" /> is used.
    /// </summary>
    public ArrayPool<byte>? BytePool { get; set; }

    /// <summary>
    ///     The <see cref="RecyclableMemoryStreamManager" /> used to rent pooled
    ///     <see cref="RecyclableMemoryStream" /> instances (e.g. to buffer a non-seekable dictionary stream given
    ///     to <see cref="VcDecoder" />). When <see langword="null" />, a shared library-wide default is used.
    /// </summary>
    public RecyclableMemoryStreamManager? MemoryStreamManager { get; set; }

	internal ArrayPool<byte> BytePoolOrDefault => this.BytePool ?? ArrayPool<byte>.Shared;

	internal RecyclableMemoryStreamManager MemoryStreamManagerOrDefault => this.MemoryStreamManager ?? DefaultMemoryStreamManager.Instance;
}