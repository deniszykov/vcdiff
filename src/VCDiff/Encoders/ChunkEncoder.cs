// Copyright (c) Metric and the Snowflake Authors.
// Licensed under the Apache License, Version 2.0.

using System;
using System.IO;
using System.Runtime.CompilerServices;
using Microsoft.IO;
using VCDiff.Shared;

namespace VCDiff.Encoders;

/// <summary>
///     Finds the dictionary matches of one target window and emits it as a VCDIFF window.
///     Reused for every window of an encode; the <see cref="BlockHash" /> is borrowed, not owned.
///     A source segment ([0, dictionarySize] by default) may be switched with <see cref="SetSourceSegment" />;
///     the borrowed hash table is then rebuilt to cover only that segment.
/// </summary>
internal sealed class ChunkEncoder : IDisposable
{
	private readonly WindowChecksumFormat checksumFormat;

	private readonly BlockHash dictionary;
	private readonly long dictionarySize;
	private readonly RabinKarpHash hasher;
	private readonly int minMatchSize;
	private readonly WindowEncoder windowEncoder;
	private bool disposed;
	private bool segmentBuilt;

    /// <param name="dictionary">The dictionary hash table. It is not disposed by this instance.</param>
    /// <param name="dictionarySize">The size of the data for the dictionary hash table</param>
    /// <param name="hash">The rolling hash object</param>
    /// <param name="checksumFormat">The format of the checksums for each window.</param>
    /// <param name="interleaved">Whether to interleave the data or not</param>
    /// <param name="minMatchSize">The minimum size of a match that is worth putting into a COPY.</param>
    /// <param name="memoryStreamManager">The recyclable memory stream manager used for window encoding.</param>
    public ChunkEncoder
	(
		BlockHash dictionary,
		long dictionarySize,
		RabinKarpHash hash,
		WindowChecksumFormat checksumFormat,
		bool interleaved,
		int minMatchSize,
		RecyclableMemoryStreamManager memoryStreamManager)
	{
		this.checksumFormat = checksumFormat;
		this.hasher = hash;
		this.dictionary = dictionary;
		this.dictionarySize = dictionarySize;
		this.minMatchSize = minMatchSize;
		this.windowEncoder = new WindowEncoder(dictionarySize, checksumFormat, interleaved, memoryStreamManager);
	}

    /// <summary>
    ///     Makes the windows that follow reference only the dictionary bytes
    ///     <c>[offset, offset + length)</c>. The hash table is rebuilt to cover just that segment, so index memory is
    ///     bounded by <paramref name="length" />. A zero <paramref name="length" /> means "no source": those windows
    ///     are emitted without a source segment.
    /// </summary>
    public void SetSourceSegment(long offset, long length)
	{
		if (offset < 0)
			throw new ArgumentOutOfRangeException(nameof(offset));
		if (length < 0)
			throw new ArgumentOutOfRangeException(nameof(length));
		if (offset > this.dictionarySize || length > this.dictionarySize - offset)
			throw new ArgumentOutOfRangeException(nameof(length), "The source segment must lie within the dictionary.");

		this.dictionary.Reset(offset, length);
		this.dictionary.AddAllBlocks();
		this.windowEncoder.SetSourceSegment(offset, length);
		this.segmentBuilt = true;
	}

	// The default full-dictionary segment is built on first use so that a caller may still call
	// SetSourceSegment (bounding the index) before the first window is encoded.
	private void EnsureSegmentBuilt()
	{
		if (this.segmentBuilt)
			return;

		this.dictionary.Reset(0, this.dictionarySize);
		this.dictionary.AddAllBlocks();
		this.segmentBuilt = true;
	}

    /// <summary>
    ///     Encodes one target window and writes it to <paramref name="outputStream" />.
    /// </summary>
    /// <param name="window">The target window, must not be empty.</param>
    /// <param name="outputStream">The stream the encoded window is written to.</param>
    public unsafe void EncodeChunk(ReadOnlySpan<byte> window, Stream outputStream)
	{
		this.EnsureSegmentBuilt();

		uint checksum = this.checksumFormat switch {
			WindowChecksumFormat.Sdch => Adler32.Hash(0, window),
			WindowChecksumFormat.Xdelta3 => Adler32.Hash(1, window),
			_ => 0
		};

		this.windowEncoder.Reset(checksum);

		fixed (byte* newDataPtr = window)
		{
			long nextEncode = 0;
			long targetEnd = window.Length;
			var startOfLastBlock = targetEnd - this.dictionary.BlockSize;
			var candidatePos = nextEncode;

			// Create the first hash
			var hash = startOfLastBlock >= 0 ? this.hasher.Hash(newDataPtr, this.dictionary.BlockSize) : 0;

			// If less than block size exit and then write as an ADD
			while (targetEnd - nextEncode >= this.dictionary.BlockSize)
			{
				//try and encode the copy and add instructions that best match
				var bytesEncoded = this.EncodeCopyForBestMatch(hash, candidatePos, nextEncode, newDataPtr, targetEnd);

				if (bytesEncoded > 0)
				{
					nextEncode += bytesEncoded;
					candidatePos = nextEncode;

					if (candidatePos > startOfLastBlock)
						break;

					//cannot use rolling hash since we skipped so many
					hash = this.hasher.Hash(newDataPtr + candidatePos, this.dictionary.BlockSize);
				}
				else
				{
					if (candidatePos + 1 > startOfLastBlock)
						break;

					//update hash requires the first byte of the last hash as well as the byte that is first byte pos + blockSize
					//in order to properly calculate the rolling hash
					var peek0 = newDataPtr[candidatePos];
					var peek1 = newDataPtr[candidatePos + this.dictionary.BlockSize];
					hash = this.hasher.UpdateHash(hash, peek0, peek1);
					candidatePos++;
				}
			}

			//Add the rest of the data that was not encoded
			if (nextEncode < targetEnd)
				this.windowEncoder.Add(new ReadOnlySpan<byte>(newDataPtr + nextEncode, (int)(targetEnd - nextEncode)));
		}

		//output the final window
		this.windowEncoder.Output(outputStream);
	}

	//currently does not support looking in target
	//only the dictionary
	[SkipLocalsInit]
	private unsafe long EncodeCopyForBestMatch(ulong hash, long candidateStart, long unencodedStart, byte* newDataPtr, long newDataLength)
	{
		var bestMatch = new BlockHash.Match();

		this.dictionary.FindBestMatch(hash, candidateStart, unencodedStart, newDataPtr, newDataLength, ref bestMatch);
		if (bestMatch.Size < this.minMatchSize) return 0;

		if (bestMatch.TOffset > 0)
			this.windowEncoder.Add(new ReadOnlySpan<byte>(newDataPtr + unencodedStart, (int)bestMatch.TOffset));

		this.windowEncoder.Copy((int)bestMatch.SOffset, (int)bestMatch.Size);

		return bestMatch.Size + bestMatch.TOffset;
	}

	public void Dispose()
	{
		if (this.disposed)
			return;

		this.disposed = true;
		this.windowEncoder.Dispose();
	}
}
