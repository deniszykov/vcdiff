// Copyright (c) Metric and the Snowflake Authors.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Buffers.Binary;
using System.IO;
using System.Runtime.CompilerServices;
using Microsoft.IO;
using VCDiff.Includes;
using VCDiff.Shared;

namespace VCDiff.Encoders;

internal sealed class WindowEncoder : IDisposable
{
	// Window indicator + 3 varint32 (dictionary size, start, delta length) + varint32 target length + delta indicator
	// + 3 varint32 section lengths + varint64 / 4 byte checksum.
	private const int MAX_HEADER_SIZE = 1 + 3 * VarIntBe.MAX_INT32_LENGTH + VarIntBe.MAX_INT32_LENGTH + 1 + 3 * VarIntBe.MAX_INT32_LENGTH + VarIntBe.MAX_INT64_LENGTH;

	// Windows are always encoded with the default code table and address cache sizes (see InstructionMap).
	private readonly AddressCache addrCache = new();

	// Pooled, block based streams: a window never needs one contiguous buffer.
	private readonly RecyclableMemoryStream addressForCopy;
	private readonly WindowChecksumFormat checksumFormat;
	private readonly RecyclableMemoryStream dataForAddAndRun;
	private readonly RecyclableMemoryStream instructionAndSizes;
	private readonly InstructionMap instrMap = InstructionMap.Instance;
	private readonly bool interleaved;
	private uint checksum;
	private bool disposed;
	private bool hasSourceSegment;
	private byte lastOpcode;
	private int lastOpcodeIndex;
	private long sourceSegmentLength;
	private long sourceSegmentOffset;
	private long targetLength;

	//This is a window encoder for the VCDIFF format
	//it is reused for every window, call Reset before encoding each one
	public WindowEncoder(long dictionarySize, WindowChecksumFormat checksumFormat, bool interleaved, RecyclableMemoryStreamManager manager)
	{
		this.checksumFormat = checksumFormat;
		this.interleaved = interleaved;
		this.sourceSegmentLength = dictionarySize;
		this.sourceSegmentOffset = 0;
		this.hasSourceSegment = true; // default: the whole dictionary, exactly as before
		this.lastOpcodeIndex = -1;

		//Separate buffers for each type if not interleaved
		if (!interleaved)
		{
			this.instructionAndSizes = manager.GetStream(nameof(WindowEncoder));
			this.dataForAddAndRun = manager.GetStream(nameof(WindowEncoder));
			this.addressForCopy = manager.GetStream(nameof(WindowEncoder));
		}
		else
			this.instructionAndSizes = this.dataForAddAndRun = this.addressForCopy = manager.GetStream(nameof(WindowEncoder));
	}

    /// <summary>
    ///     Selects the source segment (length + position within the dictionary) that the windows that follow will
    ///     reference. A zero length emits the windows without a source segment.
    /// </summary>
    public void SetSourceSegment(long offset, long length)
	{
		this.sourceSegmentOffset = offset;
		this.sourceSegmentLength = length;
		this.hasSourceSegment = length > 0;
	}

    /// <summary>
    ///     Starts a new window.
    /// </summary>
    /// <param name="checksum">The checksum of the window, ignored if no checksum format is used.</param>
    public void Reset(uint checksum)
	{
		this.checksum = checksum;
		this.addrCache.Reset();
		this.targetLength = 0;
		this.lastOpcodeIndex = -1;
		this.dataForAddAndRun.SetLength(0);
		this.instructionAndSizes.SetLength(0);
		this.addressForCopy.SetLength(0);
	}

	private void ReplaceLastOpcode(byte opcode)
	{
		var end = this.instructionAndSizes.Position;
		this.instructionAndSizes.Position = this.lastOpcodeIndex;
		this.instructionAndSizes.WriteByte(opcode);
		this.instructionAndSizes.Position = end;
	}

	[MethodImpl(MethodImplOptions.AggressiveOptimization)]
	private void EncodeInstruction(VcDiffInstructionType inst, int size, byte mode = 0)
	{
		if (this.lastOpcodeIndex >= 0)
		{
			int lastOp = this.lastOpcode;

			int compoundOp;
			if (size <= byte.MaxValue)
			{
				compoundOp = this.instrMap.LookSecondOpcode((byte)lastOp, (byte)inst, (byte)size, mode);
				if (compoundOp != CodeTable.KNoOpcode)
				{
					this.ReplaceLastOpcode((byte)compoundOp);
					this.lastOpcodeIndex = -1;
					return;
				}
			}

			compoundOp = this.instrMap.LookSecondOpcode((byte)lastOp, (byte)inst, 0, mode);
			if (compoundOp != CodeTable.KNoOpcode)
			{
				this.ReplaceLastOpcode((byte)compoundOp);

				//append size to instructionAndSizes
				VarIntBe.Append(size, this.instructionAndSizes);
				this.lastOpcodeIndex = -1;
				return;
			}
		}

		int opcode;
		if (size <= byte.MaxValue)
		{
			opcode = this.instrMap.LookFirstOpcode((byte)inst, (byte)size, mode);

			if (opcode != CodeTable.KNoOpcode)
			{
				this.instructionAndSizes.WriteByte((byte)opcode);
				this.lastOpcode = (byte)opcode;
				this.lastOpcodeIndex = (int)this.instructionAndSizes.Length - 1;
				return;
			}
		}

		opcode = this.instrMap.LookFirstOpcode((byte)inst, 0, mode);
		if (opcode == CodeTable.KNoOpcode) return;

		this.instructionAndSizes.WriteByte((byte)opcode);
		this.lastOpcode = (byte)opcode;
		this.lastOpcodeIndex = (int)this.instructionAndSizes.Length - 1;
		VarIntBe.Append(size, this.instructionAndSizes);
	}

	public void Add(ReadOnlySpan<byte> data)
	{
		this.EncodeInstruction(VcDiffInstructionType.ADD, data.Length);
		this.dataForAddAndRun.Write(data);
		this.targetLength += data.Length;
	}

	[SkipLocalsInit]
	public void Copy(int offset, int length)
	{
		var mode = this.addrCache.EncodeAddress(offset, this.sourceSegmentLength + this.targetLength, out var encodedAddr);
		this.EncodeInstruction(VcDiffInstructionType.COPY, length, mode);
		if (!this.addrCache.IsSameMode(mode))
			VarIntBe.Append(encodedAddr, this.addressForCopy);
		else
			this.addressForCopy.WriteByte((byte)encodedAddr);

		this.targetLength += length;
	}

	private int CalculateLengthOfTheDeltaEncoding()
	{
		if (this.interleaved)
		{
			return VarIntBe.GetLength(this.targetLength) +
				1 +
				VarIntBe.GetLength(0) +
				VarIntBe.GetLength(this.instructionAndSizes.Length) +
				VarIntBe.GetLength(0) +
				0 +
				(int)this.instructionAndSizes.Length

				// interleaved implies SDCH checksum if any.
				+
				(this.checksumFormat == WindowChecksumFormat.Sdch ? VarIntBe.GetLength(this.checksum) : 0);
		}

		var lengthOfDelta = VarIntBe.GetLength(this.targetLength) +
			1 +
			VarIntBe.GetLength(this.dataForAddAndRun.Length) +
			VarIntBe.GetLength(this.instructionAndSizes.Length) +
			VarIntBe.GetLength(this.addressForCopy.Length) +
			(int)this.dataForAddAndRun.Length +
			(int)this.instructionAndSizes.Length +
			(int)this.addressForCopy.Length;

		if (this.checksumFormat == WindowChecksumFormat.Sdch)
			lengthOfDelta += VarIntBe.GetLength(this.checksum);
		else if (this.checksumFormat == WindowChecksumFormat.Xdelta3) lengthOfDelta += 4;

		return lengthOfDelta;
	}

	public void Output(Stream outputStream)
	{
		var lengthOfDelta = this.CalculateLengthOfTheDeltaEncoding();

		// The window header is assembled on the stack and written with a single call,
		// the sections are then written straight from the pooled blocks of their streams.
		Span<byte> header = stackalloc byte[MAX_HEADER_SIZE];
		var pos = 0;

		//Google's Checksum Implementation Support
		if (this.hasSourceSegment)
		{
			if (this.checksumFormat != WindowChecksumFormat.None)
				header[pos++] = (byte)VcDiffWindowFlags.VCDSOURCE | (byte)VcDiffWindowFlags.VCDCHECKSUM; //win indicator
			else
				header[pos++] = (byte)VcDiffWindowFlags.VCDSOURCE; //win indicator
			pos += VarIntBe.Write(this.sourceSegmentLength, header.Slice(pos)); //source segment size
			pos += VarIntBe.Write(this.sourceSegmentOffset, header.Slice(pos)); //source segment position
		}
		else
		{
			// No source segment: the window is compressed by itself (RFC 3284 section 4.2).
			header[pos++] = this.checksumFormat != WindowChecksumFormat.None
				? (byte)VcDiffWindowFlags.VCDCHECKSUM
				: (byte)0;
		}

		pos += VarIntBe.Write(lengthOfDelta, header.Slice(pos)); //length of delta

		//begin of delta encoding
		var sizeBeforeDelta = pos;
		pos += VarIntBe.Write(this.targetLength, header.Slice(pos)); //final target length after decoding
		header[pos++] = 0x00; // uncompressed

		// [Here is where a secondary compressor would be used
		//  if the encoder and decoder supported that feature.]

		long sectionsLength;

		//non interleaved then it is separeat areas for each type
		if (!this.interleaved)
		{
			pos += VarIntBe.Write(this.dataForAddAndRun.Length, header.Slice(pos)); //length of add/run
			pos += VarIntBe.Write(this.instructionAndSizes.Length, header.Slice(pos)); //length of instructions and sizes
			pos += VarIntBe.Write(this.addressForCopy.Length, header.Slice(pos)); //length of addresses for copys

			switch (this.checksumFormat)
			{
				//Google Checksum Support
				case WindowChecksumFormat.Sdch:
					pos += VarIntBe.Write(this.checksum, header.Slice(pos));
					break;

				// Xdelta checksum support.
				case WindowChecksumFormat.Xdelta3:
					BinaryPrimitives.WriteUInt32BigEndian(header.Slice(pos), this.checksum);
					pos += sizeof(uint);
					break;
			}

			outputStream.Write(header.Slice(0, pos));
			this.dataForAddAndRun.WriteTo(outputStream); //data section for adds and runs
			this.instructionAndSizes.WriteTo(outputStream); //data for instructions and sizes
			this.addressForCopy.WriteTo(outputStream); //data for addresses section copys
			sectionsLength = this.dataForAddAndRun.Length + this.instructionAndSizes.Length + this.addressForCopy.Length;
		}
		else
		{
			//interleaved everything is woven in and out in one block
			pos += VarIntBe.Write(0, header.Slice(pos)); //length of add/run
			pos += VarIntBe.Write(this.instructionAndSizes.Length, header.Slice(pos)); //length of instructions and sizes + other data for interleaved
			pos += VarIntBe.Write(0, header.Slice(pos)); //length of addresses for copys

			//Google Checksum Support
			if (this.checksumFormat == WindowChecksumFormat.Sdch) pos += VarIntBe.Write(this.checksum, header.Slice(pos));

			outputStream.Write(header.Slice(0, pos));
			this.instructionAndSizes.WriteTo(outputStream); //data for instructions and sizes, in interleaved it is everything
			sectionsLength = this.instructionAndSizes.Length;
		}

		//end of delta encoding

		// Counted rather than read from outputStream.Position, so the output stream does not have to be seekable.
		if (lengthOfDelta != pos - sizeBeforeDelta + sectionsLength) throw VcdiffException.DeltaOutputLengthMismatch();

		this.dataForAddAndRun.SetLength(0);
		this.instructionAndSizes.SetLength(0);
		this.addressForCopy.SetLength(0);
		if (this.targetLength == 0) throw VcdiffException.EmptyTargetWindow();
	}

	public void Dispose()
	{
		if (this.disposed)
			return;

		this.disposed = true;
		this.instructionAndSizes.Dispose();
		if (!this.interleaved)
		{
			this.dataForAddAndRun.Dispose();
			this.addressForCopy.Dispose();
		}
	}
}