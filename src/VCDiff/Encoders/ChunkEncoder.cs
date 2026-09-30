// Copyright (c) Metric and the Snowflake Authors.
// Licensed under the Apache License, Version 2.0.

using System;
using System.IO;
using System.Runtime.CompilerServices;
using VCDiff.Shared;

namespace VCDiff.Encoders;

internal class ChunkEncoder : IDisposable
{
	private readonly ChecksumFormat checksumFormat;

	private readonly BlockHash dictionary;
	private readonly RollingHash hasher;
	private readonly int minBlockSize;
	private readonly WindowEncoder windowEncoder;
	private bool disposed;

    /// <summary>
    ///     Performs the actual encoding of a chunk of data into the VCDiff format
    /// </summary>
    /// <param name="dictionary">The dictionary hash table</param>
    /// <param name="dictionarySize">The size of the data for the dictionary hash table</param>
    /// <param name="hash">The rolling hash object</param>
    /// <param name="interleaved">Whether to interleave the data or not</param>
    /// <param name="checksumFormat">The format of the checksums for each window.</param>
    /// <param name="minBlockSize">
    ///     The minimum block size to use. Defaults to 32, and must be a power of 2.
    ///     This value must also be smaller than the block size of the dictionary.
    /// </param>
    public ChunkEncoder
	(
		BlockHash dictionary,
		long dictionarySize,
		RollingHash hash,
		ChecksumFormat checksumFormat,
		bool interleaved = false,
		int minBlockSize = 32)
	{
		this.checksumFormat = checksumFormat;
		this.hasher = hash;
		this.dictionary = dictionary;
		this.minBlockSize = minBlockSize;
		this.windowEncoder = new WindowEncoder(dictionarySize, checksumFormat, interleaved);
	}

	~ChunkEncoder()
	{
		this.Dispose();
	}

    /// <summary>
    ///     Encodes the data using the settings from initialization
    /// </summary>
    /// <param name="newData">the target data</param>
    /// <param name="outputStream">the out stream</param>
    public unsafe void EncodeChunk(ByteBuffer newData, Stream outputStream)
	{
		newData.Position = 0;
		var checksumBytes = newData.ReadBytesAsSpan((int)newData.Length);

		uint checksum = this.checksumFormat switch {
			ChecksumFormat.SDCH => Checksum.ComputeGoogleAdler32(checksumBytes),
			ChecksumFormat.Xdelta3 => Checksum.ComputeXdelta3Adler32(checksumBytes),
			ChecksumFormat.None => 0,
			_ => 0
		};

		this.windowEncoder.Reset(checksum);

		newData.Position = 0;

		var nextEncode = newData.Position;
		var targetEnd = newData.Length;
		var startOfLastBlock = targetEnd - this.dictionary.BlockSize;
		var candidatePos = nextEncode;
		var newDataPtr = newData.DangerousGetBytePointer();

		// Create the first hash
		var hash = startOfLastBlock >= 0 ? this.hasher.Hash(newDataPtr, this.dictionary.BlockSize) : 0;

		// If less than block size exit and then write as an ADD
		while (newData.Length - nextEncode >= this.dictionary.BlockSize)
		{
			//try and encode the copy and add instructions that best match
			var bytesEncoded = this.EncodeCopyForBestMatch(hash, candidatePos, nextEncode, newDataPtr, newData);

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
		if (nextEncode < newData.Length)
		{
			var len = (int)(newData.Length - nextEncode);
			newData.Position = nextEncode;
			this.windowEncoder.Add(newData.ReadBytesAsSpan(len));
		}

		//output the final window
		this.windowEncoder.Output(outputStream);
	}

	//currently does not support looking in target
	//only the dictionary
	[SkipLocalsInit]
	private unsafe long EncodeCopyForBestMatch(ulong hash, long candidateStart, long unencodedStart, byte* newDataPtr, ByteBuffer newData)
	{
		var bestMatch = new BlockHash.Match();

		this.dictionary.FindBestMatch(hash, candidateStart, unencodedStart, newDataPtr, newData.Length, ref bestMatch);
		if (bestMatch.Size < this.minBlockSize) return 0;

		if (bestMatch.TOffset > 0)
		{
			newData.Position = unencodedStart;
			this.windowEncoder.Add(newData.ReadBytesAsSpan((int)bestMatch.TOffset));
		}

		this.windowEncoder.Copy((int)bestMatch.SOffset, (int)bestMatch.Size);

		return bestMatch.Size + bestMatch.TOffset;
	}

	public void Dispose()
	{
		if (this.disposed)
			return;

		this.disposed = true;
		this.dictionary.Dispose();
		this.windowEncoder.Dispose();
		GC.SuppressFinalize(this);
	}
}