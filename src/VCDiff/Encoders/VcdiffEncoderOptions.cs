// Copyright (c) Denis Zykov and contributors (DeepSeek, ClaudeCode).
// Licensed under the Apache License, Version 2.0.

using System.Buffers;
using Microsoft.IO;
using VCDiff.Shared;

namespace VCDiff.Encoders;

/// <summary>
///     Options for configuring a <see cref="VcdiffEncoder" /> or <see cref="VcdiffSpanEncoder" />.
/// </summary>
public class VcdiffEncoderOptions
{
    /// <summary>
    ///     The maximum buffer size for window chunking in megabytes (MiB). Values below 1 mean 1;
    ///     values above 2047 are rejected with an <see cref="System.ArgumentOutOfRangeException" />.
    /// </summary>
    public int MaxWindowSizeMiB { get; set; } = 1;

    /// <summary>
    ///     The block size to use. Must be an even number of at least 2; a power of two is recommended.
    ///     No match smaller than this block size will be identified.
    /// </summary>
    public int BlockSize { get; set; } = 16;

    /// <summary>
    ///     The number of hash-table buckets the encoder allocates per dictionary block, controlling the
    ///     trade-off between index memory and hash collisions. The default (0) uses the open-vcdiff sizing —
    ///     one bucket per <c>sizeof(int)</c> bytes, i.e. <c>BlockSize / 4</c> buckets per block — which
    ///     over-allocates the table to reduce collisions. A value of 1 uses one bucket per block (the smallest
    ///     table); values below 1 allocate fewer buckets than blocks (more collisions, less memory). The table
    ///     is always rounded up to a power of two.
    /// </summary>
    public double HashTableSizeMultiplier { get; set; }

    /// <summary>
    ///     The minimum size of a string match that is worth putting into a COPY. This must be
    ///     at least twice the block size. Values below 2 mean twice the block size.
    /// </summary>
    public int MinMatchSize { get; set; }

    /// <summary>
    ///     A <see cref="RabinKarpHash" /> instance that can be reused across multiple encoding
    ///     instances of the same block size. If provided, the caller is responsible for
    ///     disposing it.
    /// </summary>
    public RabinKarpHash? RabinKarpHash { get; set; }

    /// <summary>
    ///     Whether to emit the SDCH interleaved format.
    /// </summary>
    /// <remarks>
    ///     Used by <see cref="VcdiffSpanEncoder" /> only. <see cref="VcdiffEncoder" /> ignores it and takes the format as an
    ///     argument of <see cref="VcdiffEncoder.Encode" /> / <see cref="VcdiffEncoder.EncodeAsync" />.
    /// </remarks>
    public bool Interleaved { get; set; }

    /// <summary>
    ///     The checksum format to emit for each window. <see cref="Shared.WindowChecksumFormat.Xdelta3" /> can not be
    ///     combined with <see cref="Interleaved" />.
    /// </summary>
    /// <remarks>
    ///     Used by <see cref="VcdiffSpanEncoder" /> only. <see cref="VcdiffEncoder" /> ignores it and takes the format as an
    ///     argument of <see cref="VcdiffEncoder.Encode" /> / <see cref="VcdiffEncoder.EncodeAsync" />.
    /// </remarks>
    public WindowChecksumFormat WindowChecksumFormat { get; set; }

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