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
	private readonly int blocksCount;
	internal readonly int BlockSize;
	private readonly RollingHash hasher;
	private readonly ulong hashTableMask;

	private readonly int maxMatchesToCheck;
	private readonly IDictionaryReader dictionaryReader;
	private bool disposed;
	private NativeAllocation<int> hashTable;
	private int lastBlockAdded;
	private NativeAllocation<int> lastBlockTable;
	private NativeAllocation<int> nextBlockTable;

    /// <summary>
    ///     Create a hash lookup table for the data
    /// </summary>
    /// <param name="dictionaryReader">the data to create the table for</param>
    /// <param name="hasher">the hashing method</param>
    /// <param name="blockSize">The block size to use</param>
    public BlockHash(IDictionaryReader dictionaryReader, RollingHash hasher, int blockSize = 16)
	{
		this.BlockSize = blockSize;
		this.maxMatchesToCheck = this.BlockSize >= 32 ? 32 : 32 * (32 / this.BlockSize);
		this.hasher = hasher;
		this.dictionaryReader = dictionaryReader;

		var tableSize = this.CalcTableSize();
		if (tableSize == 0) throw new Exception("BlockHash Table Size is Invalid == 0");

		this.blocksCount = (int)(dictionaryReader.Length / blockSize);

		this.hashTableMask = (ulong)tableSize - 1;

		this.hashTable = new NativeAllocation<int>((int)tableSize);
		this.nextBlockTable = new NativeAllocation<int>(this.blocksCount);
		this.lastBlockTable = new NativeAllocation<int>(this.blocksCount);

		this.lastBlockAdded = -1;
		this.SetTablesToInvalid();
	}

	~BlockHash()
	{
		this.Dispose();
	}

	private void SetTablesToInvalid()
	{
		this.lastBlockTable.AsSpan().Fill(-1);
		this.nextBlockTable.AsSpan().Fill(-1);
		this.hashTable.AsSpan().Fill(-1);
	}

	private long CalcTableSize()
	{
		var min = this.dictionaryReader.Length / sizeof(int) + 1;
		long size = 1;

		while (size < min)
		{
			size <<= 1;

			if (size <= 0) return 0;
		}

		if ((size & (size - 1)) != 0) return 0;

		if (this.dictionaryReader.Length > 0 && size > min * 2) return 0;

		return size;
	}

    /// <summary>
    ///     Hashes every block of the dictionary into the table.
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
			var startingOffset = (long)nextBlock * this.BlockSize;
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
			var sourceMatchOffset = (long)blockNumber * this.BlockSize;
			var sourceMatchEnd = sourceMatchOffset + this.BlockSize;
			var targetMatchOffset = candidateStart - targetStart;

			long matchSize = this.BlockSize;

			var limitBytesToLeft = Math.Min(sourceMatchOffset, targetMatchOffset);
			if (limitBytesToLeft > 0)
			{
				var leftMatching = this.dictionaryReader.MatchBackward(sourceMatchOffset, candidatePtr, limitBytesToLeft);
				sourceMatchOffset -= leftMatching;
				targetMatchOffset -= leftMatching;
				matchSize += leftMatching;
			}

			var rightLimit = Math.Min(this.dictionaryReader.Length - sourceMatchEnd, targetLength - candidateEnd);
			if (rightLimit > 0) matchSize += this.dictionaryReader.MatchForward(sourceMatchEnd, targetPtr + candidateEnd, rightLimit);

			m.ReplaceIfBetterMatch(matchSize, sourceMatchOffset, targetMatchOffset);
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
		while (blockNumber >= 0 && !this.dictionaryReader.SequenceEqual((long)blockNumber * this.BlockSize, candidatePtr, this.BlockSize))
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