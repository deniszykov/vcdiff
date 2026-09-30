using System;
using System.IO;
using VCDiff.Shared;
using System.Runtime.CompilerServices;

namespace VCDiff.Encoders
{
    internal class ChunkEncoder : IDisposable
    {
        private readonly int minBlockSize;

        private BlockHash dictionary;
        private WindowEncoder windowEncoder;
        private RollingHash hasher;
        private ChecksumFormat checksumFormat;
        private bool disposed;

        /// <summary>
        /// Performs the actual encoding of a chunk of data into the VCDiff format
        /// </summary>
        /// <param name="dictionary">The dictionary hash table</param>
        /// <param name="dictionarySize">The size of the data for the dictionary hash table</param>
        /// <param name="hash">The rolling hash object</param>
        /// <param name="interleaved">Whether to interleave the data or not</param>
        /// <param name="checksumFormat">The format of the checksums for each window.</param>
        /// <param name="minBlockSize">The minimum block size to use. Defaults to 32, and must be a power of 2.
        ///     This value must also be smaller than the block size of the dictionary.</param>
        public ChunkEncoder(BlockHash dictionary, long dictionarySize,
            RollingHash hash, ChecksumFormat checksumFormat, bool interleaved = false, int minBlockSize = 32)
        {
            this.checksumFormat = checksumFormat;
            this.hasher = hash;
            this.dictionary = dictionary;
            this.minBlockSize = minBlockSize;
            this.windowEncoder = new WindowEncoder(dictionarySize, checksumFormat, interleaved);
        }

        ~ChunkEncoder()
        {
            Dispose();
        }

        /// <summary>
        /// Encodes the data using the settings from initialization
        /// </summary>
        /// <param name="newData">the target data</param>
        /// <param name="outputStream">the out stream</param>
        public unsafe void EncodeChunk(ByteBuffer newData, Stream outputStream)
        {
            newData.Position = 0;
            var checksumBytes = newData.ReadBytesAsSpan((int)newData.Length);

            uint checksum = this.checksumFormat switch
            {
                ChecksumFormat.SDCH => Checksum.ComputeGoogleAdler32(checksumBytes),
                ChecksumFormat.Xdelta3 => Checksum.ComputeXdelta3Adler32(checksumBytes),
                ChecksumFormat.None => 0,
                _ => 0
            };

            windowEncoder.Reset(checksum);

            newData.Position = 0;

            long nextEncode = newData.Position;
            long targetEnd  = newData.Length;
            long startOfLastBlock = targetEnd - this.dictionary.blockSize;
            long candidatePos = nextEncode;
            byte* newDataPtr = newData.DangerousGetBytePointer();

            // Create the first hash
            ulong hash = startOfLastBlock >= 0 ? hasher.Hash(newDataPtr, this.dictionary.blockSize) : 0;

            // If less than block size exit and then write as an ADD
            while (newData.Length - nextEncode >= this.dictionary.blockSize)
            {
                //try and encode the copy and add instructions that best match
                var bytesEncoded = EncodeCopyForBestMatch(hash, candidatePos, nextEncode, newDataPtr, newData);

                if (bytesEncoded > 0)
                {
                    nextEncode += bytesEncoded;
                    candidatePos = nextEncode;

                    if (candidatePos > startOfLastBlock)
                        break;

                    //cannot use rolling hash since we skipped so many
                    hash = hasher.Hash(newDataPtr + candidatePos, this.dictionary.blockSize);
                }
                else
                {
                    if (candidatePos + 1 > startOfLastBlock)
                        break;

                    //update hash requires the first byte of the last hash as well as the byte that is first byte pos + blockSize
                    //in order to properly calculate the rolling hash
                    byte peek0 = newDataPtr[candidatePos];
                    byte peek1 = newDataPtr[candidatePos + this.dictionary.blockSize];
                    hash = hasher.UpdateHash(hash, peek0, peek1);
                    candidatePos++;
                }
            }

            //Add the rest of the data that was not encoded
            if (nextEncode < newData.Length)
            {
                int len = (int)(newData.Length - nextEncode);
                newData.Position = nextEncode;
                windowEncoder.Add(newData.ReadBytesAsSpan(len));
            }

            //output the final window
            windowEncoder.Output(outputStream);
        }

        //currently does not support looking in target
        //only the dictionary
        [SkipLocalsInit]
        private unsafe long EncodeCopyForBestMatch(ulong hash, long candidateStart, long unencodedStart, byte* newDataPtr, ByteBuffer newData)
        {
            BlockHash.Match bestMatch = new BlockHash.Match();

            dictionary.FindBestMatch(hash, candidateStart, unencodedStart, newDataPtr, newData.Length, ref bestMatch);
            if (bestMatch.size < minBlockSize)
            {
                return 0;
            }

            if (bestMatch.tOffset > 0)
            {
                newData.Position = unencodedStart;
                windowEncoder.Add(newData.ReadBytesAsSpan((int)bestMatch.tOffset));
            }

            windowEncoder.Copy((int)bestMatch.sOffset, (int)bestMatch.size);

            return bestMatch.size + bestMatch.tOffset;
        }

        public void Dispose()
        {
            if (disposed)
                return;

            disposed = true;
            dictionary?.Dispose();
            windowEncoder?.Dispose();
            GC.SuppressFinalize(this);
        }
    }
}
