// Copyright (c) Metric and the Snowflake Authors.
// Licensed under the Apache License, Version 2.0.

using System;
using System.IO;
using System.Runtime.CompilerServices;
using Microsoft.IO;
using VCDiff.Includes;
using VCDiff.Shared;

namespace VCDiff.Encoders;

internal class WindowEncoder : IDisposable
{
	private readonly RecyclableMemoryStream addressForCopy;
	private readonly RecyclableMemoryStream dataForAddAndRun;
	private readonly long dictionarySize;
	private readonly InstructionMap instrMap;

	// Pooled, block based streams: a window never needs one contiguous buffer.
	private readonly RecyclableMemoryStream instructionAndSizes;
	private AddressCache addrCache;
	private byte lastOpcode;
	private int lastOpcodeIndex;
	private int maxMode;
	private CodeTable table;
	private long targetLength;

	public ChecksumFormat ChecksumFormat { get; }

	public bool IsInterleaved { get; }

	public uint Checksum { get; private set; }

	//This is a window encoder for the VCDIFF format
	//it is reused for every window, call Reset before encoding each one
	public WindowEncoder(long dictionarySize, ChecksumFormat checksumFormat, bool interleaved)
	{
		this.ChecksumFormat = checksumFormat;
		this.IsInterleaved = interleaved;
		this.dictionarySize = dictionarySize;

		// The encoder currently doesn't support encoding with a custom table
		// will be added in later since it will be easy as decoding is already implemented
		this.maxMode = AddressCache.DefaultLast;
		this.table = CodeTable.DefaultTable;
		this.addrCache = new AddressCache();
		this.targetLength = 0;
		this.lastOpcodeIndex = -1;
		this.instrMap = InstructionMap.Instance;

		//Separate buffers for each type if not interleaved
		if (!interleaved)
		{
			this.instructionAndSizes = Pool.MemoryStreamManager.GetStream(nameof(WindowEncoder));
			this.dataForAddAndRun = Pool.MemoryStreamManager.GetStream(nameof(WindowEncoder));
			this.addressForCopy = Pool.MemoryStreamManager.GetStream(nameof(WindowEncoder));
		}
		else
			this.instructionAndSizes = this.dataForAddAndRun = this.addressForCopy = Pool.MemoryStreamManager.GetStream(nameof(WindowEncoder));
	}

    /// <summary>
    ///     Starts a new window.
    /// </summary>
    /// <param name="checksum">The checksum of the window, ignored if no checksum format is used.</param>
    public void Reset(uint checksum)
	{
		this.Checksum = checksum;
		this.addrCache = new AddressCache();
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
				VarIntBe.AppendInt32(size, this.instructionAndSizes);
				this.lastOpcodeIndex = -1;
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
		VarIntBe.AppendInt32(size, this.instructionAndSizes);
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
		var mode = this.addrCache.EncodeAddress(offset, this.dictionarySize + this.targetLength, out var encodedAddr);
		this.EncodeInstruction(VcDiffInstructionType.COPY, length, mode);
		if (this.addrCache.WriteAddressAsVarint(mode))
			VarIntBe.AppendInt64(encodedAddr, this.addressForCopy);
		else
			this.addressForCopy.WriteByte((byte)encodedAddr);

		this.targetLength += length;
	}

	public void Run(int size, byte b)
	{
		this.EncodeInstruction(VcDiffInstructionType.RUN, size);
		this.dataForAddAndRun.WriteByte(b);
		this.targetLength += size;
	}

	private int CalculateLengthOfTheDeltaEncoding()
	{
		if (this.IsInterleaved)
		{
			return VarIntBe.CalcInt32Length((int)this.targetLength) +
				1 +
				VarIntBe.CalcInt32Length(0) +
				VarIntBe.CalcInt32Length((int)this.instructionAndSizes.Length) +
				VarIntBe.CalcInt32Length(0) +
				0 +
				(int)this.instructionAndSizes.Length

				// interleaved implies SDCH checksum if any.
				+
				(this.ChecksumFormat == ChecksumFormat.SDCH ? VarIntBe.CalcInt64Length(this.Checksum) : 0);
		}

		var lengthOfDelta = VarIntBe.CalcInt32Length((int)this.targetLength) +
			1 +
			VarIntBe.CalcInt32Length((int)this.dataForAddAndRun.Length) +
			VarIntBe.CalcInt32Length((int)this.instructionAndSizes.Length) +
			VarIntBe.CalcInt32Length((int)this.addressForCopy.Length) +
			(int)this.dataForAddAndRun.Length +
			(int)this.instructionAndSizes.Length +
			(int)this.addressForCopy.Length;

		if (this.ChecksumFormat == ChecksumFormat.SDCH)
			lengthOfDelta += VarIntBe.CalcInt64Length(this.Checksum);
		else if (this.ChecksumFormat == ChecksumFormat.Xdelta3) lengthOfDelta += 4;

		return lengthOfDelta;
	}

	public void Output(Stream outputStream)
	{
		var lengthOfDelta = this.CalculateLengthOfTheDeltaEncoding();

		//Google's Checksum Implementation Support
		if (this.ChecksumFormat != ChecksumFormat.None)
			outputStream.WriteByte((byte)VcDiffWindowFlags.VCDSOURCE | (byte)VcDiffWindowFlags.VCDCHECKSUM); //win indicator
		else
			outputStream.WriteByte((byte)VcDiffWindowFlags.VCDSOURCE); //win indicator
		VarIntBe.AppendInt32((int)this.dictionarySize, outputStream); //dictionary size
		VarIntBe.AppendInt32(0, outputStream); //dictionary start position 0 is default aka encompass the whole dictionary

		VarIntBe.AppendInt32(lengthOfDelta, outputStream); //length of delta

		//begin of delta encoding
		var sizeBeforeDelta = outputStream.Position;
		VarIntBe.AppendInt32((int)this.targetLength, outputStream); //final target length after decoding
		outputStream.WriteByte(0x00); // uncompressed

		// [Here is where a secondary compressor would be used
		//  if the encoder and decoder supported that feature.]

		//non interleaved then it is separeat areas for each type
		if (!this.IsInterleaved)
		{
			VarIntBe.AppendInt32((int)this.dataForAddAndRun.Length, outputStream); //length of add/run
			VarIntBe.AppendInt32((int)this.instructionAndSizes.Length, outputStream); //length of instructions and sizes
			VarIntBe.AppendInt32((int)this.addressForCopy.Length, outputStream); //length of addresses for copys

			switch (this.ChecksumFormat)
			{
				//Google Checksum Support
				case ChecksumFormat.SDCH:
					VarIntBe.AppendInt64(this.Checksum, outputStream);
					break;

				// Xdelta checksum support.
				case ChecksumFormat.Xdelta3:
				{
					Span<byte> checksumBytes = stackalloc[] {
						(byte)(this.Checksum >> 24), (byte)(this.Checksum >> 16), (byte)(this.Checksum >> 8), (byte)(this.Checksum & 0x000000FF)
					};
					outputStream.Write(checksumBytes);
					break;
				}
			}

			this.dataForAddAndRun.WriteTo(outputStream); //data section for adds and runs
			this.instructionAndSizes.WriteTo(outputStream); //data for instructions and sizes
			this.addressForCopy.WriteTo(outputStream); //data for addresses section copys
		}
		else
		{
			//interleaved everything is woven in and out in one block
			VarIntBe.AppendInt32(0, outputStream); //length of add/run
			VarIntBe.AppendInt32((int)this.instructionAndSizes.Length, outputStream); //length of instructions and sizes + other data for interleaved
			VarIntBe.AppendInt32(0, outputStream); //length of addresses for copys

			//Google Checksum Support
			if (this.ChecksumFormat == ChecksumFormat.SDCH) VarIntBe.AppendInt64(this.Checksum, outputStream);

			this.instructionAndSizes.WriteTo(outputStream); //data for instructions and sizes, in interleaved it is everything
		}

		//end of delta encoding

		var sizeAfterDelta = outputStream.Position;
		if (lengthOfDelta != sizeAfterDelta - sizeBeforeDelta) throw new IOException("Delta output length does not match");

		this.dataForAddAndRun.SetLength(0);
		this.instructionAndSizes.SetLength(0);
		this.addressForCopy.SetLength(0);
		if (this.targetLength == 0) throw new IOException("Empty target window");
	}

	public void Dispose()
	{
		this.instructionAndSizes.Dispose();
		if (!this.IsInterleaved)
		{
			this.dataForAddAndRun.Dispose();
			this.addressForCopy.Dispose();
		}
	}
}