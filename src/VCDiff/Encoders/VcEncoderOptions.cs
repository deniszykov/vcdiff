// Copyright (c) Denis Zykov and contributors (DeepSeek, ClaudeCode).
// Licensed under the Apache License, Version 2.0.

using System.Buffers;
using Microsoft.IO;
using VCDiff.Shared;

namespace VCDiff.Encoders;

/// <summary>
///     Options for configuring a <see cref="VcEncoder" /> or <see cref="VcDiffEncoder" />.
/// </summary>
public class VcEncoderOptions
{
    /// <summary>
    ///     The maximum buffer size for window chunking in megabytes (MiB). Values below 1 mean 1;
    ///     values above 2047 are rejected with an <see cref="System.ArgumentOutOfRangeException" />.
    /// </summary>
    public int MaxBufferSize { get; set; } = 1;

    /// <summary>
    ///     The block size to use. Must be an even number of at least 2; a power of two is recommended.
    ///     No match smaller than this block size will be identified.
    /// </summary>
    public int BlockSize { get; set; } = 16;

    /// <summary>
    ///     The minimum size of a string match that is worth putting into a COPY. This must be
    ///     at least twice the block size. Values below 2 mean twice the block size.
    /// </summary>
    public int ChunkSize { get; set; }

    /// <summary>
    ///     A <see cref="RollingHash" /> instance that can be reused across multiple encoding
    ///     instances of the same block size. If provided, the caller is responsible for
    ///     disposing it.
    /// </summary>
    public RollingHash? RollingHash { get; set; }

    /// <summary>
    ///     Whether to emit the SDCH interleaved format.
    /// </summary>
    /// <remarks>
    ///     Used by <see cref="VcDiffEncoder" /> only. <see cref="VcEncoder" /> ignores it and takes the format as an
    ///     argument of <see cref="VcEncoder.Encode" /> / <see cref="VcEncoder.EncodeAsync" />.
    /// </remarks>
    public bool Interleaved { get; set; }

    /// <summary>
    ///     The checksum format to emit for each window. <see cref="Shared.ChecksumFormat.Xdelta3" /> can not be
    ///     combined with <see cref="Interleaved" />.
    /// </summary>
    /// <remarks>
    ///     Used by <see cref="VcDiffEncoder" /> only. <see cref="VcEncoder" /> ignores it and takes the format as an
    ///     argument of <see cref="VcEncoder.Encode" /> / <see cref="VcEncoder.EncodeAsync" />.
    /// </remarks>
    public ChecksumFormat ChecksumFormat { get; set; }

    /// <summary>
    ///     The <see cref="ArrayPool{T}" /> used to rent internal byte buffers. When
    ///     <see langword="null" />, <see cref="ArrayPool{T}.Shared" /> is used.
    /// </summary>
    public ArrayPool<byte>? BytePool { get; set; }

    /// <summary>
    ///     The <see cref="RecyclableMemoryStreamManager" /> used to rent pooled
    ///     <see cref="RecyclableMemoryStream" /> instances. When <see langword="null" />, a shared
    ///     library-wide default is used.
    /// </summary>
    public RecyclableMemoryStreamManager? MemoryStreamManager { get; set; }

	internal ArrayPool<byte> BytePoolOrDefault => this.BytePool ?? ArrayPool<byte>.Shared;

	internal RecyclableMemoryStreamManager MemoryStreamManagerOrDefault => this.MemoryStreamManager ?? DefaultMemoryStreamManager.Instance;
}