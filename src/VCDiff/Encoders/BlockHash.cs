// Copyright (c) Metric and the Snowflake Authors.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Runtime.CompilerServices;
using VCDiff.Shared;

namespace VCDiff.Encoders;

internal unsafe class BlockHash : IDisposable
{
	public ref struct Match
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
	private readonly int blocksCount;
	internal readonly int BlockSize;
	private readonly RollingHash hasher;
	private readonly ulong hashTableMask;

	private readonly int maxMatchesToCheck;
	private readonly DictionarySource source;
	private bool disposed;
	private NativeAllocation<int> hashTable;
	private int lastBlockAdded;
	private NativeAllocation<int> lastBlockTable;
	private NativeAllocation<int> nextBlockTable;

    /// <summary>
    ///     Create a hash lookup table for the data
    /// </summary>
    /// <param name="source">the data to create the table for</param>
    /// <param name="hasher">the hashing method</param>
    /// <param name="blockSize">The block size to use</param>
    public BlockHash(DictionarySource source, RollingHash hasher, int blockSize = 16)
	{
		this.BlockSize = blockSize;
		this.maxMatchesToCheck = this.BlockSize >= 32 ? 32 : 32 * (32 / this.BlockSize);
		this.hasher = hasher;
		this.source = source;

		var tableSize = this.CalcTableSize();
		if (tableSize == 0) throw new Exception("BlockHash Table Size is Invalid == 0");

		this.blocksCount = (int)(source.Length / blockSize);

		this.hashTableMask = (ulong)tableSize - 1;

		this.hashTable = new NativeAllocation<int>(tableSize);
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
		new Span<int>(this.lastBlockTable.Pointer, (int)this.lastBlockTable.NumItems).Fill(-1);
		new Span<int>(this.nextBlockTable.Pointer, (int)this.nextBlockTable.NumItems).Fill(-1);
		new Span<int>(this.hashTable.Pointer, (int)this.hashTable.NumItems).Fill(-1);
	}

	private long CalcTableSize()
	{
		var min = this.source.Length / sizeof(int) + 1;
		long size = 1;

		while (size < min)
		{
			size <<= 1;

			if (size <= 0) return 0;
		}

		if ((size & (size - 1)) != 0) return 0;

		if (this.source.Length > 0 && size > min * 2) return 0;

		return size;
	}

    /// <summary>
    ///     Hashes every block of the dictionary into the table.
    /// </summary>
    public void AddAllBlocks()
	{
		// Holds a block that straddles two dictionary segments.
		var straddle = new byte[this.BlockSize];
		fixed (byte* straddlePtr = straddle)
		{
			for (var block = this.lastBlockAdded + 1; block < this.blocksCount; block++)
			{
				var offset = (long)block * this.BlockSize;
				var ptr = this.source.GetPointer(offset, out var available);
				if (available < this.BlockSize)
				{
					this.source.CopyTo(offset, straddle);
					ptr = straddlePtr;
				}

				this.AddBlock(this.hasher.Hash(ptr, this.BlockSize));
			}
		}
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
				var leftMatching = this.source.MatchBackward(sourceMatchOffset, candidatePtr, limitBytesToLeft);
				sourceMatchOffset -= leftMatching;
				targetMatchOffset -= leftMatching;
				matchSize += leftMatching;
			}

			var rightLimit = Math.Min(this.source.Length - sourceMatchEnd, targetLength - candidateEnd);
			if (rightLimit > 0) matchSize += this.source.MatchForward(sourceMatchEnd, targetPtr + candidateEnd, rightLimit);

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
		while (blockNumber >= 0 && !this.source.SequenceEqual((long)blockNumber * this.BlockSize, candidatePtr, this.BlockSize))
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