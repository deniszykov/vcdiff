// Copyright (c) Denis Zykov and contributors (DeepSeek, ClaudeCode).
// Licensed under the Apache License, Version 2.0.

using System.Buffers;

namespace VCDiff.Decoders
{
    /// <summary>
    /// Options for configuring a <see cref="VcDecoder"/>.
    /// </summary>
    public class VcDecoderOptions
    {
        /// <summary>
        /// The maximum target file size (and target window size) in bytes.
        /// </summary>
        public int MaxTargetFileSize { get; set; } = WindowDecoderBase.DefaultMaxTargetFileSize;

        /// <summary>
        /// Whether to disable checksums when applying the delta. This can be dangerous, but can
        /// be useful when the input file differs in ways that the delta does not reference.
        /// </summary>
        public bool DisableChecksums { get; set; }

        /// <summary>
        /// The <see cref="ArrayPool{T}"/> used to rent internal byte buffers. When
        /// <see langword="null"/>, <see cref="ArrayPool{T}.Shared"/> is used.
        /// </summary>
        public ArrayPool<byte>? BytePool { get; set; }

        internal ArrayPool<byte> BytePoolOrDefault => BytePool ?? ArrayPool<byte>.Shared;
    }
}
