// Copyright (c) Metric and the Snowflake Authors.
// Licensed under the Apache License, Version 2.0.

using System.IO;
using VCDiff.Compression.Xz;

namespace VCDiff.Decoders
{
    internal class SharedDecompressors
    {
        public XZStream? AddRunDecompressor;
        public XZStream? InstructionsDecompressor;
        public XZStream? AddressesDecompressor;

        public MemoryStream? AddRunCompressedBuffer;
        public MemoryStream? InstructionsCompressedBuffer;
        public MemoryStream? AddressesCompressedBuffer;
    }
}
