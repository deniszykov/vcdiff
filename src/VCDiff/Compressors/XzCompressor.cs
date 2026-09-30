using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Text;
using VCDiff.Compression;
using VCDiff.Compression.Xz;
using VCDiff.Shared;

namespace VCDiff.Compressors
{
    internal class XzCompressor : ICompressor, IDisposable
    {
        private readonly ArrayPool<byte> _bytePool;

        public XzCompressor(ArrayPool<byte>? bytePool = null)
        {
            _bytePool = bytePool ?? ArrayPool<byte>.Shared;

            addRunCompressedBuffer = new();
            instructionsCompressedBuffer = new();
            addressesCompressedBuffer = new();

            addRunDecompressor = new(addRunCompressedBuffer, _bytePool);
            instructionsDecompressor = new(instructionsCompressedBuffer, _bytePool);
            addressesDecompressor = new(addressesCompressedBuffer, _bytePool);
        }

        private readonly MemoryStream addRunCompressedBuffer;
        private readonly MemoryStream instructionsCompressedBuffer;
        private readonly MemoryStream addressesCompressedBuffer;
        private readonly XZStream addRunDecompressor;
        private readonly XZStream instructionsDecompressor;
        private readonly XZStream addressesDecompressor;

        public PinnedArrayRental Decompress(WindowSectionType windowSectionType, PinnedArrayRental sectionData)
        {
            if (sectionData.Data == null)
            {
                throw new ArgumentException("Cannot decompress null data");
            }

            MemoryStream memoryStream;
            XZStream xzStream;
            switch (windowSectionType)
            {
                case WindowSectionType.AddRunData:
                    memoryStream = addRunCompressedBuffer;
                    xzStream = addRunDecompressor;
                    break;
                case WindowSectionType.InstructionsAndSizes:
                    memoryStream = instructionsCompressedBuffer;
                    xzStream = instructionsDecompressor;
                    break;
                case WindowSectionType.AddressForCopy:
                    memoryStream = addressesCompressedBuffer;
                    xzStream = addressesDecompressor;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(windowSectionType));
            }

            var uncompressedLength = VarIntBE.ParseInt32(sectionData.AsSpan(), out int uncompressedLengthByteCount);
            var compressedData = sectionData.AsSpan().Slice(uncompressedLengthByteCount);

            // Each section in a window uses the same compression stream throughout the file
            // If this is not the first window, reuse the same stream from before, just using different data
            memoryStream.SetLength(compressedData.Length);
            memoryStream.Position = 0;
            memoryStream.Write(compressedData);
            memoryStream.Position = 0;

            var decompressedData = new PinnedArrayRental(uncompressedLength, _bytePool);
            xzStream.ReadExactly(decompressedData.AsSpan());

            return decompressedData;
        }
        public void Dispose()
        {
            addressesCompressedBuffer?.Dispose();
            instructionsCompressedBuffer?.Dispose();
            addressesCompressedBuffer?.Dispose();
            addRunDecompressor?.Dispose();
            instructionsDecompressor?.Dispose();
            addressesDecompressor?.Dispose();
        }
    }
}
