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
