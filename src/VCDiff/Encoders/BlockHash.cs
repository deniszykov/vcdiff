using System;
using System.Runtime.CompilerServices;
using VCDiff.Shared;

namespace VCDiff.Encoders
{
    internal unsafe class BlockHash : IDisposable
    {
        internal readonly int blockSize;

        private readonly int maxMatchesToCheck;
        private const int maxProbes = 16;
        private readonly ulong hashTableMask;
        private int lastBlockAdded;
        private NativeAllocation<int> hashTable;
        private NativeAllocation<int> nextBlockTable;
        private NativeAllocation<int> lastBlockTable;
        private readonly RollingHash hasher;
        private readonly DictionarySource source;
        private readonly int blocksCount;
        private bool disposed;

        /// <summary>
        /// Create a hash lookup table for the data
        /// </summary>
        /// <param name="source">the data to create the table for</param>
        /// <param name="hasher">the hashing method</param>
        /// <param name="blockSize">The block size to use</param>
        public BlockHash(DictionarySource source, RollingHash hasher, int blockSize = 16)
        {
            this.blockSize = blockSize;
            this.maxMatchesToCheck = (this.blockSize >= 32) ? 32 : (32 * (32 / this.blockSize));
            this.hasher = hasher;
            this.source = source;

            long tableSize = CalcTableSize();
            if (tableSize == 0)
            {
                throw new Exception("BlockHash Table Size is Invalid == 0");
            }

            this.blocksCount = (int)(source.Length / blockSize);

            hashTableMask = (ulong)tableSize - 1;

            hashTable = new NativeAllocation<int>(tableSize);
            nextBlockTable = new NativeAllocation<int>(blocksCount);
            lastBlockTable = new NativeAllocation<int>(blocksCount);

            lastBlockAdded = -1;
            SetTablesToInvalid();
        }

        ~BlockHash()
        {
            Dispose();
        }

        private void SetTablesToInvalid()
        {
            new Span<int>(lastBlockTable.Pointer, (int)lastBlockTable.NumItems).Fill(-1);
            new Span<int>(nextBlockTable.Pointer, (int)nextBlockTable.NumItems).Fill(-1);
            new Span<int>(hashTable.Pointer, (int)hashTable.NumItems).Fill(-1);
        }

        private long CalcTableSize()
        {
            long min = (this.source.Length / sizeof(int)) + 1;
            long size = 1;

            while (size < min)
            {
                size <<= 1;

                if (size <= 0)
                {
                    return 0;
                }
            }

            if ((size & (size - 1)) != 0)
            {
                return 0;
            }

            if ((source.Length > 0) && (size > (min * 2)))
            {
                return 0;
            }
            return size;
        }

        /// <summary>
        /// Hashes every block of the dictionary into the table.
        /// </summary>
        public void AddAllBlocks()
        {
            // Holds a block that straddles two dictionary segments.
            byte[] straddle = new byte[blockSize];
            fixed (byte* straddlePtr = straddle)
            {
                for (int block = lastBlockAdded + 1; block < blocksCount; block++)
                {
                    long offset = (long)block * blockSize;
                    byte* ptr = source.GetPointer(offset, out long available);
                    if (available < blockSize)
                    {
                        source.CopyTo(offset, straddle);
                        ptr = straddlePtr;
                    }

                    AddBlock(hasher.Hash(ptr, blockSize));
                }
            }
        }

        /// <summary>
        /// Finds the best matching block for the candidate
        /// </summary>
        /// <param name="hash">the hash to look for</param>
        /// <param name="candidateStart">the start position</param>
        /// <param name="targetStart">the target start position</param>
        /// <param name="targetPtr">pointer to the target buffer</param>
        /// <param name="targetLength">the length of the target buffer</param>
        /// <param name="m">the match object to use</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        [SkipLocalsInit]
        public void FindBestMatch(ulong hash, long candidateStart, long targetStart, byte* targetPtr, long targetLength, ref Match m)
        {
            int matchCounter = 0;
            byte* candidatePtr = targetPtr + candidateStart;
            long candidateEnd = candidateStart + blockSize;

            for (int blockNumber = SkipNonMatchingBlocks(hashTable.Pointer[(long)(hash & hashTableMask)], candidatePtr);
                blockNumber >= 0 && !TooManyMatches(ref matchCounter);
                blockNumber = SkipNonMatchingBlocks(nextBlockTable.Pointer[blockNumber], candidatePtr))
            {
                long sourceMatchOffset = (long)blockNumber * blockSize;
                long sourceMatchEnd = sourceMatchOffset + blockSize;
                long targetMatchOffset = candidateStart - targetStart;

                long matchSize = blockSize;

                long limitBytesToLeft = Math.Min(sourceMatchOffset, targetMatchOffset);
                if (limitBytesToLeft > 0)
                {
                    long leftMatching = source.MatchBackward(sourceMatchOffset, candidatePtr, limitBytesToLeft);
                    sourceMatchOffset -= leftMatching;
                    targetMatchOffset -= leftMatching;
                    matchSize += leftMatching;
                }

                long rightLimit = Math.Min(source.Length - sourceMatchEnd, targetLength - candidateEnd);
                if (rightLimit > 0)
                {
                    matchSize += source.MatchForward(sourceMatchEnd, targetPtr + candidateEnd, rightLimit);
                }

                m.ReplaceIfBetterMatch(matchSize, sourceMatchOffset, targetMatchOffset);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void AddBlock(ulong hash)
        {
            int blockNumber = lastBlockAdded + 1;
            if (blockNumber >= blocksCount)
            {
                return;
            }

            if (nextBlockTable.Pointer[blockNumber] != -1)
            {
                return;
            }

            long tableIndex = (long)(hash & hashTableMask);
            int firstMatching = hashTable.Pointer[tableIndex];
            if (firstMatching < 0)
            {
                hashTable.Pointer[tableIndex] = blockNumber;
                lastBlockTable.Pointer[blockNumber] = blockNumber;
            }
            else
            {
                int lastMatching = lastBlockTable.Pointer[firstMatching];
                if (nextBlockTable.Pointer[lastMatching] != -1)
                {
                    return;
                }
                nextBlockTable.Pointer[lastMatching] = blockNumber;
                lastBlockTable.Pointer[firstMatching] = blockNumber;
            }
            lastBlockAdded = blockNumber;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        [SkipLocalsInit]
        private int SkipNonMatchingBlocks(int blockNumber, byte* candidatePtr)
        {
            int probes = 0;
            int* next = nextBlockTable.Pointer;
            while ((blockNumber >= 0) && !source.SequenceEqual((long)blockNumber * blockSize, candidatePtr, blockSize))
            {
                if (++probes > maxProbes)
                {
                    return -1;
                }
                blockNumber = next[blockNumber];
            }
            return blockNumber;
        }

        private bool TooManyMatches(ref int matchCounter)
        {
            ++matchCounter;
            return (matchCounter > maxMatchesToCheck);
        }

        public ref struct Match
        {
            public long size;
            public long sOffset;
            public long tOffset;

            public void ReplaceIfBetterMatch(long csize, long sourcOffset, long targetOffset)
            {
                if (csize <= size) return;
                size = csize;
                sOffset = sourcOffset;
                tOffset = targetOffset;
            }
        }

        public void Dispose()
        {
            if (disposed)
                return;

            disposed = true;
            hashTable.Dispose();
            nextBlockTable.Dispose();
            lastBlockTable.Dispose();
            GC.SuppressFinalize(this);
        }
    }
}
