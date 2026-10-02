// Copyright (c) Metric and the Snowflake Authors.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using VCDiff.Shared;

namespace VCDiff.Encoders;

internal sealed unsafe class BlockHash : IDisposable
{
	internal ref struct Match
	{
		public long Size;
		public long SOffset;
		public long TOffset;

		public void ReplaceIfBetterMatch(long csize, long sourcOffset, long targetOffset)
		{
			if (csize <= this.Size) return;

			this.Size = csize;
			this.SOffset = sourcOffset;
			this.TOffset = targetOffset;
		}
	}

	private const int MAX_PROBES = 16;
	private const int MAX_BYTES_PER_DICTIONARY_READ = 1 * 1024 * 1024; // 1Mib
	internal readonly int BlockSize;
	private readonly RabinKarpHash hasher;
	private readonly int maxMatchesToCheck;
	private readonly ISourceReader dictionaryReader;

	// Buckets per dictionary block: 0 (or <= 0) uses the open-vcdiff default of one bucket per sizeof(int)
	// bytes; a positive value over-allocates the bucket table by that factor relative to one bucket per block.
	private readonly double hashTableSizeMultiplier;
	private bool disposed;

	// The dictionary sub-range [segmentOffset, segmentOffset + segmentLength) that this table indexes.
	// block numbers are relative to the segment, and SOffset is reported relative to it too.
	private long segmentOffset;
	private long segmentLength;

	private int blocksCount;
	private ulong hashTableMask;
	private NativeAllocation<int> hashTable;
	private int lastBlockAdded;
	private NativeAllocation<int> lastBlockTable;
	private NativeAllocation<int> nextBlockTable;

    /// <summary>
    ///     Create a hash lookup table for the data. The table is sized and filled lazily by
    ///     <see cref="Reset" /> and <see cref="AddAllBlocks" />, so no allocation is made until the segment to
    ///     index is known.
    /// </summary>
    /// <param name="dictionaryReader">the data to create the table for</param>
    /// <param name="hasher">the hashing method</param>
    /// <param name="blockSize">The block size to use</param>
    /// <param name="hashTableSizeMultiplier">
    ///     Buckets per dictionary block; 0 (or negative) selects the open-vcdiff default of one bucket per
    ///     <c>sizeof(int)</c> bytes.
    /// </param>
    public BlockHash(ISourceReader dictionaryReader, RabinKarpHash hasher, int blockSize = 16, double hashTableSizeMultiplier = 0)
	{
		this.BlockSize = blockSize;
		this.maxMatchesToCheck = this.BlockSize >= 32 ? 32 : 32 * (32 / this.BlockSize);
		this.hasher = hasher;
		this.dictionaryReader = dictionaryReader;
		this.hashTableSizeMultiplier = hashTableSizeMultiplier;
	}

	~BlockHash()
	{
		this.Dispose();
	}

    /// <summary>
    ///     Selects the dictionary sub-range to index and (re)sizes the tables for it. Existing allocations are
    ///     reused when the new segment fits them. Blocks are not hashed until <see cref="AddAllBlocks" /> is called.
    /// </summary>
    /// <param name="segmentOffset">The start of the segment within the dictionary.</param>
    /// <param name="segmentLength">The length of the segment. Zero means no source data.</param>
    public void Reset(long segmentOffset, long segmentLength)
	{
		if (segmentOffset < 0)
			throw new ArgumentOutOfRangeException(nameof(segmentOffset));
		if (segmentLength < 0)
			throw new ArgumentOutOfRangeException(nameof(segmentLength));
		if (segmentOffset > this.dictionaryReader.Length || segmentLength > this.dictionaryReader.Length - segmentOffset)
			throw new ArgumentOutOfRangeException(nameof(segmentLength), "The source segment must lie within the dictionary.");

		this.segmentOffset = segmentOffset;
		this.segmentLength = segmentLength;
		this.blocksCount = (int)(segmentLength / this.BlockSize);

		var tableSize = this.CalcTableSize();
		if (tableSize == 0) throw VcdiffException.BlockHashTableSizeInvalid();

		this.hashTableMask = (ulong)tableSize - 1;
		this.EnsureCapacity((int)tableSize, this.blocksCount);

		this.lastBlockAdded = -1;
		this.SetTablesToInvalid();
	}

	private void EnsureCapacity(int tableSize, int blockCount)
	{
		if (this.hashTable.Pointer == null || this.hashTable.Length < tableSize)
		{
			var old = this.hashTable;
			this.hashTable = new NativeAllocation<int>(tableSize);
			old.Dispose();
		}

		if (this.nextBlockTable.Pointer == null || this.nextBlockTable.Length < blockCount)
		{
			var old = this.nextBlockTable;
			this.nextBlockTable = new NativeAllocation<int>(blockCount);
			old.Dispose();
		}

		if (this.lastBlockTable.Pointer == null || this.lastBlockTable.Length < blockCount)
		{
			var old = this.lastBlockTable;
			this.lastBlockTable = new NativeAllocation<int>(blockCount);
			old.Dispose();
		}
	}

	private void SetTablesToInvalid()
	{
		this.lastBlockTable.AsSpan().Fill(-1);
		this.nextBlockTable.AsSpan().Fill(-1);
		this.hashTable.AsSpan().Fill(-1);
	}

	private long CalcTableSize()
	{
		long min;
		if (this.hashTableSizeMultiplier <= 0)
		{
			// open-vcdiff default: over-allocate the table to one bucket per sizeof(int) bytes, so empty
			// entries cut the probability of a hash collision to sizeof(int) / BlockSize.
			min = this.segmentLength / sizeof(int) + 1;
		}
		else
		{
			// One bucket per block is the smallest table; the multiplier over-allocates beyond that. Probing
			// stays bounded by MAX_PROBES / maxMatchesToCheck, so a smaller table trades ratio for memory.
			var buckets = this.hashTableSizeMultiplier * this.blocksCount;
			if (!double.IsFinite(buckets) || buckets >= int.MaxValue) return 0;

			if (buckets < 1) buckets = 1;

			min = (long)buckets + 1;
		}

		long size = 1;

		while (size < min)
		{
			size <<= 1;

			if (size <= 0 || size > int.MaxValue) return 0;
		}

		if ((size & (size - 1)) != 0) return 0;

		if (this.segmentLength > 0 && size > min * 2) return 0;

		return size;
	}

    /// <summary>
    ///     Hashes every block of the current segment into the table.
    /// </summary>
    [SkipLocalsInit]
    public void AddAllBlocks()
	{
		byte* straddle = stackalloc byte[this.BlockSize];
		var carryOverBytes = 0;
		var nextBlock = this.lastBlockAdded + 1;

		while (nextBlock < this.blocksCount)
		{
			// Read a whole number of blocks so a chunk never ends mid-block; the straddle buffer only
			// ever bridges two segments within one chunk.
			var blocksRemaining = this.blocksCount - nextBlock;
			var blocksInChunk = Math.Min(blocksRemaining, Math.Max(1, MAX_BYTES_PER_DICTIONARY_READ / this.BlockSize));
			var startingOffset = this.segmentOffset + (long)nextBlock * this.BlockSize;
			var bytesToRead = (long)blocksInChunk * this.BlockSize;

			foreach (var segment in this.dictionaryReader.Read(startingOffset, bytesToRead))
			{
				using var pinnedMemory = segment.Pin();
				var pinnedMemoryPtr = (byte*)pinnedMemory.Pointer;
				var pinnedMemoryOffset = 0;

				// Finish the block carried over from the previous segment.
				if (carryOverBytes > 0)
				{
					var toCopyIntoStraddle = Math.Min(segment.Length, this.BlockSize - carryOverBytes);
					Unsafe.CopyBlockUnaligned(straddle + carryOverBytes, pinnedMemoryPtr, (uint)toCopyIntoStraddle);
					pinnedMemoryOffset += toCopyIntoStraddle;
					carryOverBytes += toCopyIntoStraddle;

					if (carryOverBytes < this.BlockSize)
						continue; // segment exhausted before the block was completed
				}

				if (carryOverBytes == this.BlockSize)
				{
					this.AddBlock(this.hasher.Hash(straddle, this.BlockSize));
					carryOverBytes = 0;
				}

				// Hash whole blocks directly from the segment.
				while (segment.Length - pinnedMemoryOffset >= this.BlockSize)
				{
					this.AddBlock(this.hasher.Hash(pinnedMemoryPtr + pinnedMemoryOffset, this.BlockSize));
					pinnedMemoryOffset += this.BlockSize;
				}

				// Carry the partial tail over to the next segment.
				carryOverBytes = segment.Length - pinnedMemoryOffset;
				if (carryOverBytes > 0)
					Unsafe.CopyBlockUnaligned(straddle, pinnedMemoryPtr + pinnedMemoryOffset, (uint)carryOverBytes);
			}

			nextBlock = this.lastBlockAdded + 1;
		}

		Debug.Assert(carryOverBytes == 0, "Every block lies within one chunk.");
	}

    /// <summary>
    ///     Finds the best matching block for the candidate
    /// </summary>
    /// <param name="hash">the hash to look for</param>
    /// <param name="candidateStart">the start position</param>
    /// <param name="targetStart">the target start position</param>
    /// <param name="targetPtr">pointer to the target buffer</param>
    /// <param name="targetLength">the length of the target buffer</param>
    /// <param name="m">the match object to use</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization), SkipLocalsInit]
	public void FindBestMatch(ulong hash, long candidateStart, long targetStart, byte* targetPtr, long targetLength, ref Match m)
	{
		var matchCounter = 0;
		var candidatePtr = targetPtr + candidateStart;
		var candidateEnd = candidateStart + this.BlockSize;

		for (var blockNumber = this.SkipNonMatchingBlocks(this.hashTable.Pointer[(long)(hash & this.hashTableMask)], candidatePtr);
			blockNumber >= 0 && !this.TooManyMatches(ref matchCounter);
			blockNumber = this.SkipNonMatchingBlocks(this.nextBlockTable.Pointer[blockNumber], candidatePtr))
		{
			var sourceMatchOffset = this.segmentOffset + (long)blockNumber * this.BlockSize;
			var sourceMatchEnd = sourceMatchOffset + this.BlockSize;
			var targetMatchOffset = candidateStart - targetStart;

			long matchSize = this.BlockSize;

			// A match must never extend outside the active segment (or the target window).
			var limitBytesToLeft = Math.Min(sourceMatchOffset - this.segmentOffset, targetMatchOffset);
			if (limitBytesToLeft > 0)
			{
				var leftMatching = this.dictionaryReader.MatchBackward(sourceMatchOffset, candidatePtr, limitBytesToLeft);
				sourceMatchOffset -= leftMatching;
				targetMatchOffset -= leftMatching;
				matchSize += leftMatching;
			}

			var rightLimit = Math.Min(this.segmentLength - (sourceMatchEnd - this.segmentOffset), targetLength - candidateEnd);
			if (rightLimit > 0) matchSize += this.dictionaryReader.MatchForward(sourceMatchEnd, targetPtr + candidateEnd, rightLimit);

			// SOffset is reported relative to the segment: the COPY address of a per-window source segment.
			m.ReplaceIfBetterMatch(matchSize, sourceMatchOffset - this.segmentOffset, targetMatchOffset);
		}
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private void AddBlock(ulong hash)
	{
		var blockNumber = this.lastBlockAdded + 1;
		if (blockNumber >= this.blocksCount) return;

		if (this.nextBlockTable.Pointer[blockNumber] != -1) return;

		var tableIndex = (long)(hash & this.hashTableMask);
		var firstMatching = this.hashTable.Pointer[tableIndex];
		if (firstMatching < 0)
		{
			this.hashTable.Pointer[tableIndex] = blockNumber;
			this.lastBlockTable.Pointer[blockNumber] = blockNumber;
		}
		else
		{
			var lastMatching = this.lastBlockTable.Pointer[firstMatching];
			if (this.nextBlockTable.Pointer[lastMatching] != -1) return;

			this.nextBlockTable.Pointer[lastMatching] = blockNumber;
			this.lastBlockTable.Pointer[firstMatching] = blockNumber;
		}

		this.lastBlockAdded = blockNumber;
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization), SkipLocalsInit]
	private int SkipNonMatchingBlocks(int blockNumber, byte* candidatePtr)
	{
		var probes = 0;
		var next = this.nextBlockTable.Pointer;
		while (blockNumber >= 0 && !this.dictionaryReader.SequenceEqual(this.segmentOffset + (long)blockNumber * this.BlockSize, candidatePtr, this.BlockSize))
		{
			if (++probes > MAX_PROBES) return -1;

			blockNumber = next[blockNumber];
		}

		return blockNumber;
	}

	private bool TooManyMatches(ref int matchCounter)
	{
		++matchCounter;
		return matchCounter > this.maxMatchesToCheck;
	}

	public void Dispose()
	{
		if (this.disposed)
			return;

		this.disposed = true;
		this.hashTable.Dispose();
		this.nextBlockTable.Dispose();
		this.lastBlockTable.Dispose();
		GC.SuppressFinalize(this);
	}
}
