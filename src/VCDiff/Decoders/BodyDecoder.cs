// Copyright (c) Metric and the Snowflake Authors.
// Licensed under the Apache License, Version 2.0.

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using VCDiff.Includes;
using VCDiff.Shared;

namespace VCDiff.Decoders;

internal class BodyDecoder<TWindowDecoderByteBufferT, TSourceBufferT, TDeltaBufferT> : IDisposable
	where TWindowDecoderByteBufferT : IByteBuffer
	where TSourceBufferT : IByteBuffer
	where TDeltaBufferT : IByteBuffer
{
	// Read the source in small pieces so a stream backed source never allocates a buffer as large as the copy.
	private const int MAX_SOURCE_READ = 8192;
	private readonly AddressCache addressCache;
	private readonly CustomCodeTableDecoder? customTable;
	private readonly bool disableChecksums;
	private readonly Stream outputStream;
	private readonly MemoryStream targetData;
	private readonly WindowDecoder<TWindowDecoderByteBufferT> window;
	private TDeltaBufferT delta;
	private TSourceBufferT source;

	//the total bytes decoded
	public long TotalBytesDecoded { get; private set; }

    /// <summary>
    ///     The main decoder loop for the data
    /// </summary>
    /// <param name="w">the window decoder</param>
    /// <param name="source">The source dictionary data</param>
    /// <param name="delta">The delta</param>
    /// <param name="decodedTarget">the out stream</param>
    /// <param name="customTable">custom table if any. Default is null.</param>
    /// <param name="disableChecksums">Whether to disable checksum validation.</param>
    public BodyDecoder
	(
		WindowDecoder<TWindowDecoderByteBufferT> w,
		TSourceBufferT source,
		TDeltaBufferT delta,
		Stream decodedTarget,
		CustomCodeTableDecoder? customTable = null,
		bool disableChecksums = false)
	{
		if (customTable != null)
		{
			this.customTable = customTable;
			this.addressCache = new AddressCache(customTable.NearSize, customTable.SameSize);
		}
		else
			this.addressCache = new AddressCache();

		this.window = w;
		this.outputStream = decodedTarget;
		this.source = source;
		this.delta = delta;
		this.targetData = Pool.MemoryStreamManager.GetStream(nameof(BodyDecoder<TWindowDecoderByteBufferT, TSourceBufferT, TDeltaBufferT>), (int)w.TargetWindowLength);
		this.disableChecksums = disableChecksums;
	}

	private VcDiffResult DecodeInterleaveCore()
	{
		var result = VcDiffResult.SUCCESS;

		//since interleave expected then the last point that was most likely decoded was the lengths section
		//so following is all data for the add run copy etc
		var interleaveLength = this.window.InstructionAndSizesLength;
		using var previous = Pool.MemoryStreamManager.GetStream(nameof(BodyDecoder<TWindowDecoderByteBufferT, TSourceBufferT, TDeltaBufferT>), (int)interleaveLength);
		var lastDecodedSize = 0;
		var lastDecodedInstruction = VcDiffInstructionType.NOOP;

		while (interleaveLength > 0)
		{
			if (!this.delta.CanRead) continue;

			//read in
			var didBreakBeforeComplete = false;

			//try to read in all interleaved bytes
			//if not then it will buffer for next time
			previous.Write(this.delta.ReadBytesAsSpan((int)interleaveLength));
			using var incoming = new ByteBuffer(previous.GetBuffer());
			previous.SetLength(0);
			var initialLength = incoming.Length;

			var instrDecoder = new InstructionDecoder(incoming, this.customTable);

			while (incoming.CanRead && this.TotalBytesDecoded < this.window.TargetWindowLength)
			{
				var decodedSize = 0;
				byte mode = 0;
				var instruction = VcDiffInstructionType.NOOP;

				if (lastDecodedSize > 0 && lastDecodedInstruction != VcDiffInstructionType.NOOP)
				{
					decodedSize = lastDecodedSize;
					instruction = lastDecodedInstruction;
				}
				else
				{
					instruction = instrDecoder.Next(out decodedSize, out mode);

					switch (instruction)
					{
						case VcDiffInstructionType.EOD:
							didBreakBeforeComplete = true;
							break;

						case VcDiffInstructionType.ERROR:
							this.targetData.SetLength(0);
							return VcDiffResult.ERROR;
					}
				}

				//if instruction is EOD then decodedSize will be 0 as well
				//the last part of the buffer containing the instruction will be
				//buffered for the next loop
				lastDecodedInstruction = instruction;
				lastDecodedSize = decodedSize;

				if (didBreakBeforeComplete)
				{
					//we don't have all the data so store this pointer into a temporary list to resolve next loop
					didBreakBeforeComplete = true;
					interleaveLength -= incoming.Position;

					if (initialLength - incoming.Position > 0) previous.Write(incoming.ReadBytesAsSpan((int)(initialLength - incoming.Position)));

					break;
				}

				// ReSharper disable once SwitchStatementHandlesSomeKnownEnumValuesWithDefault
				switch (instruction)
				{
					case VcDiffInstructionType.ADD:
						result = this.DecodeAdd(decodedSize, incoming);
						break;

					case VcDiffInstructionType.RUN:
						result = this.DecodeRun(decodedSize, incoming);
						break;

					case VcDiffInstructionType.COPY:
						result = this.DecodeCopy(decodedSize, mode, incoming);
						break;

					default:
						this.targetData.SetLength(0);
						return VcDiffResult.ERROR;
				}

				if (result == VcDiffResult.EOD)
				{
					//we don't have all the data so store this pointer into a temporary list to resolve next loop
					didBreakBeforeComplete = true;
					interleaveLength -= incoming.Position;

					if (initialLength - incoming.Position > 0) previous.Write(incoming.ReadBytesAsSpan((int)(initialLength - incoming.Position)));

					break;
				}

				//reset these as we have successfully used them
				lastDecodedInstruction = VcDiffInstructionType.NOOP;
				lastDecodedSize = 0;
			}

			if (!didBreakBeforeComplete) interleaveLength -= initialLength;
		}

		if (this.window.ChecksumFormat == ChecksumFormat.SDCH)
		{
			var adler = Checksum.ComputeGoogleAdler32(this.targetData.GetBuffer().AsSpan(0, (int)this.targetData.Length));

			if (adler != this.window.Checksum) result = VcDiffResult.ERROR;
		}

		return result;
	}

    /// <summary>
    ///     Decode if as expecting interleave
    /// </summary>
    /// <returns></returns>
    public VcDiffResult DecodeInterleave()
	{
		var result = this.DecodeInterleaveCore();
		this.targetData.Seek(0, SeekOrigin.Begin);
		this.targetData.CopyTo(this.outputStream);
		this.targetData.SetLength(0);
		return result;
	}

    /// <summary>
    ///     Decode if as expecting interleave
    /// </summary>
    /// <returns></returns>
    public async Task<VcDiffResult> DecodeInterleaveAsync(CancellationToken token = default)
	{
		var result = this.DecodeInterleaveCore();
		this.targetData.Seek(0, SeekOrigin.Begin);
		await this.targetData.CopyToAsync(this.outputStream, token);
		this.targetData.SetLength(0);
		return result;
	}

	private VcDiffResult DecodeCore()
	{
		using var instructionBuffer = new ByteBuffer(this.window.InstructionsAndSizesData.AsSpanOrDefault());
		using var addressBuffer = new ByteBuffer(this.window.AddressesForCopyData.AsSpanOrDefault());
		using var addRunBuffer = new ByteBuffer(this.window.AddRunData.AsSpanOrDefault());

		var instrDecoder = new InstructionDecoder(instructionBuffer, this.customTable);

		var result = VcDiffResult.SUCCESS;

		while (this.TotalBytesDecoded < this.window.TargetWindowLength)
		{
			var instruction = instrDecoder.Next(out var decodedSize, out var mode);

			switch (instruction)
			{
				case VcDiffInstructionType.EOD:
					this.targetData.SetLength(0);
					return VcDiffResult.EOD;

				case VcDiffInstructionType.ERROR:
					this.targetData.SetLength(0);
					return VcDiffResult.ERROR;
			}

			// ReSharper disable once SwitchStatementHandlesSomeKnownEnumValuesWithDefault
			switch (instruction)
			{
				case VcDiffInstructionType.ADD:
					result = this.DecodeAdd(decodedSize, addRunBuffer);
					break;

				case VcDiffInstructionType.RUN:
					result = this.DecodeRun(decodedSize, addRunBuffer);
					break;

				case VcDiffInstructionType.COPY:
					result = this.DecodeCopy(decodedSize, mode, addressBuffer);
					break;

				default:
					this.targetData.SetLength(0);
					return VcDiffResult.ERROR;
			}
		}

		if (this.window.ChecksumFormat == ChecksumFormat.SDCH)
		{
			var adler = Checksum.ComputeGoogleAdler32(this.targetData.GetBuffer().AsSpan(0, (int)this.targetData.Length));

			if (adler != this.window.Checksum && !this.disableChecksums) result = VcDiffResult.ERROR;
		}
		else if (this.window.ChecksumFormat == ChecksumFormat.Xdelta3)
		{
			var adler = Checksum.ComputeXdelta3Adler32(this.targetData.GetBuffer().AsSpan(0, (int)this.targetData.Length));

			if (adler != this.window.Checksum && !this.disableChecksums) result = VcDiffResult.ERROR;
		}

		return result;
	}

    /// <summary>
    ///     Decode normally
    /// </summary>
    /// <returns></returns>
    public VcDiffResult Decode()
	{
		var result = this.DecodeCore();
		this.targetData.Seek(0, SeekOrigin.Begin);
		this.targetData.CopyTo(this.outputStream);
		this.targetData.SetLength(0);
		return result;
	}

    /// <summary>
    ///     Decode normally
    /// </summary>
    /// <returns></returns>
    public async Task<VcDiffResult> DecodeAsync(CancellationToken token = default)
	{
		var result = this.DecodeCore();
		this.targetData.Seek(0, SeekOrigin.Begin);
		await this.targetData.CopyToAsync(this.outputStream, token);
		this.targetData.SetLength(0);
		return result;
	}

	private VcDiffResult DecodeCopy(int size, byte mode, ByteBuffer addresses)
	{
		var hereAddress = this.window.SourceSegmentLength + this.TotalBytesDecoded;
		var decodedAddress = this.addressCache.DecodeAddress(hereAddress, mode, addresses);
		// ReSharper disable once SwitchStatementHandlesSomeKnownEnumValuesWithDefault
		switch ((VcDiffResult)decodedAddress)
		{
			case VcDiffResult.ERROR:
				return VcDiffResult.ERROR;

			case VcDiffResult.EOD:
				return VcDiffResult.EOD;

			default:
				if (decodedAddress < 0 || decodedAddress > hereAddress) return VcDiffResult.ERROR;

				break;
		}

		// Copy all data from source segment
		if (decodedAddress + size <= this.window.SourceSegmentLength)
		{
			if (!this.CopyFromSource(decodedAddress + this.window.SourceSegmentOffset, size))
				return VcDiffResult.ERROR;

			this.TotalBytesDecoded += size;
			return VcDiffResult.SUCCESS;
		}

		// Copy some data from target window...
		if (decodedAddress < this.window.SourceSegmentLength)
		{
			// ... plus some data from source segment
			var partialCopySize = this.window.SourceSegmentLength - decodedAddress;
			if (!this.CopyFromSource(decodedAddress + this.window.SourceSegmentOffset, partialCopySize))
				return VcDiffResult.ERROR;

			this.TotalBytesDecoded += partialCopySize;
			decodedAddress += partialCopySize;
			size -= (int)partialCopySize;
		}

		decodedAddress -= this.window.SourceSegmentLength;
		var overlap = decodedAddress + size >= this.TotalBytesDecoded;
		if (overlap)
		{
			var availableData = (int)(this.TotalBytesDecoded - decodedAddress);
			for (var i = 0; i < size; i += availableData)
			{
				var toCopy = size - i < availableData ? size - i : availableData;
				var tbytesBuf = this.targetData.GetBuffer().AsSpan((int)decodedAddress + i, toCopy);

				//outputStream.Write(tbytesBuf);
				this.targetData.Write(tbytesBuf);
				this.TotalBytesDecoded += toCopy;
			}
		}
		else
		{
			var fbytes = this.targetData.GetBuffer().AsSpan((int)decodedAddress, size);

			//outputStream.Write(fbytes);
			this.targetData.Write(fbytes);
			this.TotalBytesDecoded += size;
		}

		return VcDiffResult.SUCCESS;
	}

	private bool CopyFromSource(long position, long size)
	{
		this.source.Position = position;
		while (size > 0)
		{
			var bytes = this.source.ReadBytesAsSpan((int)Math.Min(size, MAX_SOURCE_READ));
			if (bytes.IsEmpty)
				return false;

			this.targetData.Write(bytes);
			size -= bytes.Length;
		}

		return true;
	}

	private VcDiffResult DecodeRun(int size, ByteBuffer addRun)
	{
		if (addRun.Position + 1 > addRun.Length) return VcDiffResult.EOD;

		if (!addRun.CanRead) return VcDiffResult.EOD;

		var b = addRun.ReadByte();

		for (var i = 0; i < size; ++i)

			//outputStream.Write(b);
			this.targetData.WriteByte(b);

		this.TotalBytesDecoded += size;

		return VcDiffResult.SUCCESS;
	}

	private VcDiffResult DecodeAdd(int size, ByteBuffer addRun)
	{
		if (addRun.Position + size > addRun.Length) return VcDiffResult.EOD;

		if (!addRun.CanRead) return VcDiffResult.EOD;

		this.targetData.Write(addRun.ReadBytesAsSpan(size));
		this.TotalBytesDecoded += size;
		return VcDiffResult.SUCCESS;
	}

	public void Dispose()
	{
		this.targetData.Dispose();
	}
}