// Copyright (c) Metric and the Snowflake Authors.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Buffers;
using System.Diagnostics;
using VCDiff.Compressors;
using VCDiff.Includes;
using VCDiff.Shared;

namespace VCDiff.Decoders;

internal class WindowDecoderBase
{
	/**
	* The default maximum target file size (and target window size)
	*/
	public const int DEFAULT_MAX_TARGET_FILE_SIZE = 67108864; // 64 MB
}

internal class WindowDecoder<TByteBufferT> : WindowDecoderBase, IDisposable where TByteBufferT : IByteBuffer
{
	public struct ParseableChunk
	{
		private long position;

		public long UnparsedSize => this.End - this.position;

		public long End { get; }

		public bool IsEmpty => 0 == this.UnparsedSize;

		public long Start { get; }

		public long ParsedSize => this.position - this.Start;

		public long Position
		{
			get => this.position;
			set
			{
				if (this.position < this.Start) return;

				if (this.position > this.End) return;

				this.position = value;
			}
		}

		public ParseableChunk(long s, long len)
		{
			this.Start = s;
			this.End = s + len;
			this.position = s;
		}
	}

	private readonly ArrayPool<byte> _bytePool;
	private readonly long dictionarySize;
	private readonly int maxWindowSize;
	private readonly ICompressor? secondaryCompressor;
	private long _targetLength;

	public PinnedArrayRental AddressesForCopyData;
	private long addressForCopyLength;

	public PinnedArrayRental AddRunData;
	private long addRunLength;

	private TByteBufferT buffer;
	private uint checksum;
	private ParseableChunk chunk;
	private long deltaEncodingLength;
	private long deltaEncodingStart;
	private byte deltaIndicator;
	private long instructionAndSizesLength;

	public PinnedArrayRental InstructionsAndSizesData;
	private long sourceSegmentLength;
	private long sourceSegmentOffset;
	private byte winIndicator;

	public long AddRunLength => this.addRunLength;

	public long InstructionAndSizesLength => this.instructionAndSizesLength;

	public long AddressesForCopyLength => this.addressForCopyLength;

	public bool AddRunCompressed { get; private set; }

	public bool InstructionAndSizesCompressed { get; private set; }

	public bool AddressesForCopyCompressed { get; private set; }

	public byte WinIndicator => this.winIndicator;

	public long SourceSegmentOffset => this.sourceSegmentOffset;

	public long SourceSegmentLength => this.sourceSegmentLength;

	public long TargetWindowLength => this._targetLength;

	public uint Checksum => this.checksum;

	public ChecksumFormat ChecksumFormat { get; private set; }

	public int Result { get; private set; }

    /// <summary>
    ///     Parses the window from the data
    /// </summary>
    /// <param name="dictionarySize">the dictionary size</param>
    /// <param name="buffer">the buffer containing the incoming data</param>
    /// <param name="maxWindowSize">The maximum target window size in bytes</param>
    /// <param name="secondaryCompressor">The secondary compressor that can decompress window sections, if applicable.</param>
    /// <param name="bytePool">The array pool used to allocate buffers for the window section data.</param>
    public WindowDecoder
		(long dictionarySize, TByteBufferT buffer, ICompressor? secondaryCompressor, int maxWindowSize = DEFAULT_MAX_TARGET_FILE_SIZE, ArrayPool<byte>? bytePool = null)
	{
		this.dictionarySize = dictionarySize;
		this.buffer = buffer;
		this.secondaryCompressor = secondaryCompressor;
		this._bytePool = bytePool ?? ArrayPool<byte>.Shared;
		this.chunk = new ParseableChunk(buffer.Position, buffer.Length);

		if (maxWindowSize < 0) throw new ArgumentException("maxWindowSize must be a positive value", "maxWindowSize");

		this.maxWindowSize = maxWindowSize;

		this.Result = (int)VcDiffResult.SUCCESS;
	}

    /// <summary>
    ///     Decodes the window header.
    /// </summary>
    /// <param name="isSdch">If the delta uses SDCH extensions.</param>
    /// <param name="secondaryCompressorId">ID of the secondary compressor.</param>
    /// <returns></returns>
    public bool Decode(bool isSdch, byte secondaryCompressorId)
	{
		if (!this.ParseWindowIndicatorAndSegment(this.dictionarySize, 0, false, out this.winIndicator, out this.sourceSegmentLength,
				out this.sourceSegmentOffset)) return false;

		if (!this.ParseWindowLengths(out this._targetLength)) return false;

		if (!this.ParseDeltaIndicator()) return false;

		this.ChecksumFormat = ChecksumFormat.None;
		if ((this.winIndicator & (int)VcDiffWindowFlags.VCDCHECKSUM) != 0) this.ChecksumFormat = isSdch ? ChecksumFormat.SDCH : ChecksumFormat.Xdelta3;

		if (!this.ParseSectionLengths(this.ChecksumFormat, out this.addRunLength, out this.instructionAndSizesLength, out this.addressForCopyLength,
				out this.checksum)) return false;

		if (isSdch && this.addRunLength == 0 && this.addressForCopyLength == 0 && this.instructionAndSizesLength > 0)
		{
			//interleave format
			return true;
		}

		// Note: Copied required here due to caching behaviour.
		if (this.buffer.CanRead)
		{
			this.AddRunData = new PinnedArrayRental((int)this.addRunLength, this._bytePool);
			Debug.Assert(this.addRunLength <= int.MaxValue);
			this.buffer.ReadBytesToSpan(this.AddRunData.AsSpan());
			if (this.AddRunCompressed && secondaryCompressorId != 0)
			{
				if (this.secondaryCompressor == null)
					throw new InvalidOperationException("AddRunData is compressed but no compressor was provided to the WindowDecoder");

				this.AddRunData = this.secondaryCompressor.Decompress(WindowSectionType.AddRunData, this.AddRunData);
			}
		}

		if (this.buffer.CanRead)
		{
			this.InstructionsAndSizesData = new PinnedArrayRental((int)this.instructionAndSizesLength, this._bytePool);
			Debug.Assert(this.instructionAndSizesLength <= int.MaxValue);
			this.buffer.ReadBytesToSpan(this.InstructionsAndSizesData.AsSpan());
			if (this.InstructionAndSizesCompressed && secondaryCompressorId != 0)
			{
				if (this.secondaryCompressor == null)
					throw new InvalidOperationException("AddRunData is compressed but no compressor was provided to the WindowDecoder");

				this.InstructionsAndSizesData = this.secondaryCompressor.Decompress(WindowSectionType.InstructionsAndSizes, this.InstructionsAndSizesData);
			}
		}

		if (this.buffer.CanRead)
		{
			this.AddressesForCopyData = new PinnedArrayRental((int)this.addressForCopyLength, this._bytePool);
			Debug.Assert(this.addressForCopyLength <= int.MaxValue);
			this.buffer.ReadBytesToSpan(this.AddressesForCopyData.AsSpan());
			if (this.AddressesForCopyCompressed && secondaryCompressorId != 0)
			{
				if (this.secondaryCompressor == null)
					throw new InvalidOperationException("AddRunData is compressed but no compressor was provided to the WindowDecoder");

				this.AddressesForCopyData = this.secondaryCompressor.Decompress(WindowSectionType.AddressForCopy, this.AddressesForCopyData);
			}
		}

		return true;
	}

	private bool ParseByte(out byte value)
	{
		if ((int)VcDiffResult.SUCCESS != this.Result)
		{
			value = 0;
			return false;
		}

		if (this.chunk.IsEmpty)
		{
			value = 0;
			this.Result = (int)VcDiffResult.EOD;
			return false;
		}

		value = this.buffer.ReadByte();
		this.chunk.Position = this.buffer.Position;
		return true;
	}

	private bool ParseInt32(out int value)
	{
		if ((int)VcDiffResult.SUCCESS != this.Result)
		{
			value = 0;
			return false;
		}

		if (this.chunk.IsEmpty)
		{
			value = 0;
			this.Result = (int)VcDiffResult.EOD;
			return false;
		}

		var parsed = VarIntBe.ParseInt32(this.buffer);
		switch (parsed)
		{
			case (int)VcDiffResult.ERROR:
				value = 0;
				return false;

			case (int)VcDiffResult.EOD:
				value = 0;
				return false;
		}

		this.chunk.Position = this.buffer.Position;
		value = parsed;
		return true;
	}

	private bool ParseUInt32(out uint value)
	{
		if ((int)VcDiffResult.SUCCESS != this.Result)
		{
			value = 0;
			return false;
		}

		if (this.chunk.IsEmpty)
		{
			value = 0;
			this.Result = (int)VcDiffResult.EOD;
			return false;
		}

		var parsed = VarIntBe.ParseInt64(this.buffer);
		switch (parsed)
		{
			case (int)VcDiffResult.ERROR:
				value = 0;
				return false;

			case (int)VcDiffResult.EOD:
				value = 0;
				return false;
		}

		if (parsed > 0xFFFFFFFF)
		{
			this.Result = (int)VcDiffResult.ERROR;
			value = 0;
			return false;
		}

		this.chunk.Position = this.buffer.Position;
		value = (uint)parsed;
		return true;
	}

	private bool ParseSourceSegmentLengthAndPosition(long from, out long sourceLength, out long sourcePosition)
	{
		if (!this.ParseInt32(out var outLength))
		{
			sourceLength = 0;
			sourcePosition = 0;
			return false;
		}

		sourceLength = outLength;
		if (sourceLength > from)
		{
			this.Result = (int)VcDiffResult.ERROR;
			sourceLength = 0;
			sourcePosition = 0;
			return false;
		}

		if (!this.ParseInt32(out var outPos))
		{
			sourcePosition = 0;
			sourceLength = 0;
			return false;
		}

		sourcePosition = outPos;
		if (sourcePosition > from)
		{
			this.Result = (int)VcDiffResult.ERROR;
			sourceLength = 0;
			sourcePosition = 0;
			return false;
		}

		var segmentEnd = sourcePosition + sourceLength;
		if (segmentEnd > from)
		{
			this.Result = (int)VcDiffResult.ERROR;
			sourceLength = 0;
			sourcePosition = 0;
			return false;
		}

		return true;
	}

	private bool ParseWindowIndicatorAndSegment
		(long dictionarySize, long decodedTargetSize, bool allowVcdTarget, out byte winIndicator, out long sourceSegmentLength, out long sourceSegmentPosition)
	{
		if (!this.ParseByte(out winIndicator))
		{
			winIndicator = 0;
			sourceSegmentLength = 0;
			sourceSegmentPosition = 0;
			return false;
		}

		var sourceFlags = winIndicator & ((int)VcDiffWindowFlags.VCDSOURCE | (int)VcDiffWindowFlags.VCDTARGET);

		switch (sourceFlags)
		{
			case 0:
				sourceSegmentPosition = 0;
				sourceSegmentLength = 0;
				return true;

			case (int)VcDiffWindowFlags.VCDSOURCE:
				return this.ParseSourceSegmentLengthAndPosition(dictionarySize, out sourceSegmentLength, out sourceSegmentPosition);

			case (int)VcDiffWindowFlags.VCDTARGET:
				if (!allowVcdTarget)
				{
					winIndicator = 0;
					sourceSegmentLength = 0;
					sourceSegmentPosition = 0;
					this.Result = (int)VcDiffResult.ERROR;
					return false;
				}

				return this.ParseSourceSegmentLengthAndPosition(decodedTargetSize, out sourceSegmentLength, out sourceSegmentPosition);

			case (int)VcDiffWindowFlags.VCDSOURCE | (int)VcDiffWindowFlags.VCDTARGET:
				winIndicator = 0;
				sourceSegmentPosition = 0;
				sourceSegmentLength = 0;
				return false;
		}

		winIndicator = 0;
		sourceSegmentPosition = 0;
		sourceSegmentLength = 0;
		return false;
	}

	private bool ParseWindowLengths(out long targetWindowLength)
	{
		if (!this.ParseInt32(out var deltaLength))
		{
			targetWindowLength = 0;
			return false;
		}

		this.deltaEncodingLength = deltaLength;

		this.deltaEncodingStart = this.chunk.ParsedSize;
		if (!this.ParseInt32(out var outTargetLength))
		{
			targetWindowLength = 0;
			return false;
		}

		targetWindowLength = outTargetLength;
		if (targetWindowLength > this.maxWindowSize)
		{
			targetWindowLength = 0;
			this.Result = (int)VcDiffResult.ERROR;
			throw new InvalidOperationException(string.Format("Length of target window ({0}) exceeds limit of {1} bytes", outTargetLength, this.maxWindowSize));
		}

		return true;
	}

	private bool ParseDeltaIndicator()
	{
		if (!this.ParseByte(out this.deltaIndicator))
		{
			this.Result = (int)VcDiffResult.ERROR;
			return false;
		}

		this.AddRunCompressed = (this.deltaIndicator & (int)VcDiffCompressFlags.VCDDATACOMP) != 0;
		this.InstructionAndSizesCompressed = (this.deltaIndicator & (int)VcDiffCompressFlags.VCDINSTCOMP) != 0;
		this.AddressesForCopyCompressed = (this.deltaIndicator & (int)VcDiffCompressFlags.VCDADDRCOMP) != 0;

		return true;
	}

	public bool ParseSectionLengths(ChecksumFormat checksumFormat, out long addRunLength, out long instructionsLength, out long addressLength, out uint checksum)
	{
		this.ParseInt32(out var outAdd);
		this.ParseInt32(out var outInstruct);
		this.ParseInt32(out var outAddress);
		checksum = 0;

		if (checksumFormat == ChecksumFormat.SDCH)
			this.ParseUInt32(out checksum);
		else if (checksumFormat == ChecksumFormat.Xdelta3)
		{
			// xdelta checksum is stored as a 4-part byte array
			this.ParseByte(out var chk0);
			this.ParseByte(out var chk1);
			this.ParseByte(out var chk2);
			this.ParseByte(out var chk3);
			checksum = (uint)((chk0 << 24) | (chk1 << 16) | (chk2 << 8) | chk3);
		}

		addRunLength = outAdd;
		addressLength = outAddress;
		instructionsLength = outInstruct;

		if (this.Result != (int)VcDiffResult.SUCCESS) return false;

		var deltaHeaderLength = this.chunk.ParsedSize - this.deltaEncodingStart;
		var totalLen = deltaHeaderLength + addRunLength + instructionsLength + addressLength;

		if (this.deltaEncodingLength == totalLen) return true;

		this.Result = (int)VcDiffResult.ERROR;
		return false;
	}

	public void Dispose()
	{
		this.AddRunData.Dispose();
		this.InstructionsAndSizesData.Dispose();
		this.AddressesForCopyData.Dispose();
	}
}