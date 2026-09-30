// Copyright (c) Denis Zykov and contributors (DeepSeek, ClaudeCode).
// Licensed under the Apache License, Version 2.0.

using System;
using System.Buffers;
using VCDiff.Compressors;
using VCDiff.Includes;
using VCDiff.Shared;

namespace VCDiff.Decoders;

/// <summary>
///     A stateful, span-based streaming VCDIFF decoder that follows the zlib-style
///     <see cref="OperationStatus" /> contract.
/// </summary>
/// <remarks>
///     <para>
///         Feed delta bytes via <see cref="Decode" /> and collect the decoded target bytes from the
///         <c>output</c> span. Only a small amount of input is buffered internally, so when
///         <see cref="OperationStatus.DestinationTooSmall" /> is returned <c>inputConsumed</c> may be less
///         than the length of <c>input</c>. <see cref="OperationStatus.Done" /> is returned once, when
///         <c>isFinal</c> is set and the entire delta has been decoded and emitted.
///     </para>
///     <para>
///         The dictionary is not copied: its segments are pinned and read in place, so it may be made of
///         many small buffers. The caller must keep it alive and must not mutate it until
///         <see cref="Dispose" /> is called.
///     </para>
/// </remarks>
public sealed class VcDiffDecoder : IDisposable
{
	private enum ParseStatus
	{
		Ok,
		NeedMore,
		Error
	}

	private const byte DEFAULT_NEAR_SIZE = 4;
	private const byte DEFAULT_SAME_SIZE = 3;
	private const byte FIRST_NEAR = (byte)VcDiffModes.FIRST;

	// How much unparsed delta is kept between calls before more is taken from the caller.
	private const int INPUT_BUFFER_SIZE = 16 * 1024;

	private readonly DictionarySource _dictionary;
	private readonly bool _disableChecksums;
	private readonly StreamingByteBuffer _input;
	private readonly int _maxTargetWindowSize;
	private readonly ArrayPool<byte> _pool;
	private bool _addressesCompressed;
	private PinnedArrayRental _addressesData;
	private long _addressForCopyLength;
	private int _addrPos;
	private bool _addRunCompressed;
	private PinnedArrayRental _addRunData;
	private long _addRunLength;
	private int _addRunPos;
	private uint _checksum;
	private ChecksumFormat _checksumFormat;
	private CustomCodeTableDecoder? _customTable;

	private bool _disposed;
	private bool _hasPendingInstruction;

	// File header state.
	private bool _headerParsed;
	private int _instrPos;
	private long _instructionAndSizesLength;
	private bool _instructionsCompressed;
	private PinnedArrayRental _instructionsData;
	private long _interleavedRemaining;
	private bool _interleavedWindow;
	private bool isSdch;
	private long[] _nearCache = new long[DEFAULT_NEAR_SIZE];

	// Address cache state (per window).
	private byte _nearSize = DEFAULT_NEAR_SIZE;
	private int _nextSlot;
	private byte _pendingInstructionMode;
	private int _pendingInstructionSize;
	private VcDiffInstructionType _pendingInstructionType;

	// Instruction decode state.
	private int _pendingSecondOpcode = CodeTable.KNoOpcode;
	private uint _runningChecksum;
	private long[] _sameCache = new long[DEFAULT_SAME_SIZE * 256];
	private byte _sameSize = DEFAULT_SAME_SIZE;
	private XzCompressor? _secondaryCompressor;
	private byte _secondaryCompressorId;
	private int _sectionFilled;
	private int _sectionIndex;

	// Window body state.
	private bool _sectionsBuffered;
	private PinnedArrayRental _sectionWire;
	private long _sourceSegmentLength;
	private long _sourceSegmentOffset;
	private int _targetDecoded;
	private int _targetEmitted;

	// Target output state.
	private byte[]? _targetWindow;
	private int _targetWindowLength;

	// Window header state.
	private bool _windowHeaderParsed;

	// The unread part of the interleaved window body that has arrived so far.
	private ReadOnlySpan<byte> InterleavedData
	{
		get
		{
			var data = this._input.Remaining;
			return data.Length > this._interleavedRemaining ? data.Slice(0, (int)this._interleavedRemaining) : data;
		}
	}

	// ------------------------------------------------------------------ address cache

	private byte FirstSame => (byte)(VcDiffModes.FIRST + this._nearSize);

	private byte Last => (byte)(this.FirstSame + this._sameSize - 1);

    /// <summary>
    ///     Creates a streaming VCDIFF decoder.
    /// </summary>
    /// <param name="dictionary">
    ///     The dictionary (source/base) data. It is referenced, not copied, and must outlive this
    ///     instance.
    /// </param>
    /// <param name="options">The decoder options. See <see cref="VcDecoderOptions" />.</param>
    public VcDiffDecoder(ReadOnlySequence<byte> dictionary, VcDecoderOptions? options = null)
	{
		options ??= new VcDecoderOptions();

		this._pool = options.BytePoolOrDefault;
		this._maxTargetWindowSize = options.MaxTargetFileSize;
		this._disableChecksums = options.DisableChecksums;

		if (this._maxTargetWindowSize <= 0)
			throw new ArgumentOutOfRangeException(nameof(options), "MaxTargetFileSize must be positive.");

		this._dictionary = new DictionarySource(dictionary);
		this._input = new StreamingByteBuffer(8192, this._pool);
	}

    /// <summary>
    ///     Decodes as much delta data as possible, writing target bytes into <paramref name="output" />.
    /// </summary>
    /// <param name="input">The delta data to decode.</param>
    /// <param name="output">The destination for target bytes.</param>
    /// <param name="inputConsumed">The number of <paramref name="input" /> bytes consumed.</param>
    /// <param name="outputWritten">The number of bytes written to <paramref name="output" />.</param>
    /// <param name="isFinal">Whether this is the final chunk of delta data.</param>
    /// <returns>The transformation status.</returns>
    public OperationStatus Decode(ReadOnlySpan<byte> input, Span<byte> output, out int inputConsumed, out int outputWritten, bool isFinal)
	{
		if (this._disposed)
			throw new ObjectDisposedException(nameof(VcDiffDecoder));

		inputConsumed = 0;
		outputWritten = 0;

		var stalled = false;
		while (true)
		{
			// Take only as much input as fits the internal buffer, unless the decoder could
			// not make progress with a full one (e.g. a large application header).
			var room = INPUT_BUFFER_SIZE - (int)this._input.Available;
			if (room <= 0 && stalled)
				room = INPUT_BUFFER_SIZE;

			var take = Math.Min(input.Length - inputConsumed, Math.Max(room, 0));
			if (take > 0)
			{
				this._input.Append(input.Slice(inputConsumed, take));
				inputConsumed += take;
			}

			var final = isFinal && inputConsumed == input.Length;
			var status = this.DecodeCore(output.Slice(outputWritten), out var written, final);
			outputWritten += written;
			this._input.Compact();

			if (status != OperationStatus.NeedMoreData || inputConsumed == input.Length)
				return status;

			stalled = this._input.Available >= INPUT_BUFFER_SIZE;
		}
	}

	private OperationStatus DecodeCore(Span<byte> output, out int outputWritten, bool isFinal)
	{
		outputWritten = 0;

		while (true)
		{
			if (!this._headerParsed)
			{
				var hs = this.ParseHeader();
				if (hs == ParseStatus.NeedMore)
					return isFinal ? OperationStatus.InvalidData : OperationStatus.NeedMoreData;
				if (hs == ParseStatus.Error)
					return OperationStatus.InvalidData;
			}

			// Emit any pending decoded target bytes.
			var emitted = this.EmitTarget(output.Slice(outputWritten));
			outputWritten += emitted;
			if (this._targetEmitted < this._targetDecoded)
				return OperationStatus.DestinationTooSmall;

			// Parse the next window header.
			if (!this._windowHeaderParsed)
			{
				if (this._input.Available == 0)
					return isFinal ? OperationStatus.Done : OperationStatus.NeedMoreData;

				var hs = this.ParseWindowHeader();
				if (hs == ParseStatus.NeedMore)
					return isFinal ? OperationStatus.InvalidData : OperationStatus.NeedMoreData;
				if (hs == ParseStatus.Error)
					return OperationStatus.InvalidData;
			}

			// Buffer the full delta window for non-interleaved format.
			if (!this._interleavedWindow && !this._sectionsBuffered)
			{
				var bs = this.BufferSections();
				if (bs == ParseStatus.NeedMore)
					return isFinal ? OperationStatus.InvalidData : OperationStatus.NeedMoreData;
				if (bs == ParseStatus.Error)
					return OperationStatus.InvalidData;
			}

			// If the current window is fully decoded, finalize it and move on.
			if (this._targetDecoded == this._targetWindowLength)
			{
				if (this._interleavedWindow && this._interleavedRemaining != 0)
					return OperationStatus.InvalidData;
				if (!this.VerifyChecksum())
					return OperationStatus.InvalidData;

				this.FinishWindow();
				continue;
			}

			// Decode a single instruction.
			var ok = this._interleavedWindow
				? this.DecodeInterleavedInstruction(out var needMore)
				: this.DecodeNonInterleavedInstruction(out needMore);

			if (!ok)
			{
				if (needMore)
					return isFinal ? OperationStatus.InvalidData : OperationStatus.NeedMoreData;

				return OperationStatus.InvalidData;
			}
		}
	}

	// ------------------------------------------------------------------ header

	private ParseStatus ParseHeader()
	{
		var data = this._input.Remaining;
		var i = 0;

		if (data.Length < 5)
			return ParseStatus.NeedMore;

		byte v = data[0], c = data[1], d = data[2], version = data[3], hdr = data[4];
		if (v != 0xD6 || c != 0xC3 || d != 0xC4)
			return ParseStatus.Error;
		if (version != 0x00 && version != (byte)'S')
			return ParseStatus.Error;

		i = 5;

		byte secondaryId = 0;
		if ((hdr & (int)VcDiffCodeFlags.VCDDECOMPRESS) != 0)
		{
			if (data.Length < 6)
				return ParseStatus.NeedMore;

			secondaryId = data[5];
			i = 6;
		}

		CustomCodeTableDecoder? customTable = null;
		if ((hdr & (int)VcDiffCodeFlags.VCDCODETABLE) != 0)
		{
			var len = VarIntBe.ParseInt32(data.Slice(i), out var lenBytes);
			if (len == (int)VcDiffResult.EOD)
				return ParseStatus.NeedMore;
			if (len == (int)VcDiffResult.ERROR || len <= 0)
				return ParseStatus.Error;
			if (i + lenBytes + len > data.Length)
				return ParseStatus.NeedMore;

			var ctBytes = data.Slice(i, lenBytes + len).ToArray();
			using var ctb = new ByteBuffer(ctBytes);
			var ctd = new CustomCodeTableDecoder();
			if (ctd.Decode(ctb) != VcDiffResult.SUCCESS)
				return ParseStatus.Error;

			customTable = ctd;
			i += lenBytes + len;
		}

		if ((hdr & (int)VcDiffCodeFlags.VCDAPPHEADER) != 0)
		{
			var st = ReadVarInt(data, ref i, out var appLen);
			if (st != ParseStatus.Ok)
				return st;
			if (appLen < 0)
				return ParseStatus.Error;
			if (i + appLen > data.Length)
				return ParseStatus.NeedMore;

			i += appLen;
		}

		if (secondaryId != 0 && secondaryId != 2)
			return ParseStatus.Error;

		this._input.Skip(i);
		this._headerParsed = true;
		this.isSdch = version == (byte)'S';
		this._secondaryCompressorId = secondaryId;
		this._customTable = customTable;
		this._nearSize = customTable?.NearSize ?? DEFAULT_NEAR_SIZE;
		this._sameSize = customTable?.SameSize ?? DEFAULT_SAME_SIZE;
		this._nearCache = new long[this._nearSize];
		this._sameCache = new long[this._sameSize * 256];
		this._nextSlot = 0;

		if (secondaryId == 2) this._secondaryCompressor ??= new XzCompressor(this._pool);

		return ParseStatus.Ok;
	}

	// ------------------------------------------------------------------ window header

	private ParseStatus ParseWindowHeader()
	{
		var data = this._input.Remaining;
		var i = 0;

		if (data.Length < 1)
			return ParseStatus.NeedMore;

		var winIndicator = data[i++];

		var sourceFlags = winIndicator & ((int)VcDiffWindowFlags.VCDSOURCE | (int)VcDiffWindowFlags.VCDTARGET);
		long sourceLength = 0, sourceOffset = 0;
		switch (sourceFlags)
		{
			case 0:
				break;
			case (int)VcDiffWindowFlags.VCDSOURCE:
				var st1 = ReadVarInt(data, ref i, out var sl);
				if (st1 != ParseStatus.Ok) return st1;

				sourceLength = sl;
				var st2 = ReadVarInt(data, ref i, out var so);
				if (st2 != ParseStatus.Ok) return st2;

				sourceOffset = so;
				break;
			case (int)VcDiffWindowFlags.VCDTARGET:
				return ParseStatus.Error;
			default:
				return ParseStatus.Error;
		}

		if (sourceLength < 0 ||
			sourceOffset < 0 ||
			sourceLength > this._dictionary.Length ||
			sourceOffset > this._dictionary.Length ||
			sourceOffset + sourceLength > this._dictionary.Length)
			return ParseStatus.Error;

		var st3 = ReadVarInt(data, ref i, out var deltaLength);
		if (st3 != ParseStatus.Ok) return st3;

		long deltaEncodingLength = deltaLength;
		var deltaEncodingStart = i;

		var st4 = ReadVarInt(data, ref i, out var targetLength);
		if (st4 != ParseStatus.Ok) return st4;
		if (targetLength < 0 || targetLength > this._maxTargetWindowSize)
			return ParseStatus.Error;

		if (i >= data.Length)
			return ParseStatus.NeedMore;

		var deltaIndicator = data[i++];

		var st5 = ReadVarInt(data, ref i, out var addRunLength);
		if (st5 != ParseStatus.Ok) return st5;

		var st6 = ReadVarInt(data, ref i, out var instrLength);
		if (st6 != ParseStatus.Ok) return st6;

		var st7 = ReadVarInt(data, ref i, out var addrLength);
		if (st7 != ParseStatus.Ok) return st7;
		if (addRunLength < 0 || instrLength < 0 || addrLength < 0)
			return ParseStatus.Error;

		// Sections are buffered whole, so do not let a corrupt header ask for an oversized buffer.
		if (addRunLength > this._maxTargetWindowSize || instrLength > this._maxTargetWindowSize || addrLength > this._maxTargetWindowSize)
			return ParseStatus.Error;

		uint checksum = 0;
		var cf = ChecksumFormat.None;
		if ((winIndicator & (int)VcDiffWindowFlags.VCDCHECKSUM) != 0)
		{
			cf = this.isSdch ? ChecksumFormat.SDCH : ChecksumFormat.Xdelta3;
			if (this.isSdch)
			{
				var parsed = VarIntBe.ParseInt64(data.Slice(i), out var vb);
				if (parsed == (long)VcDiffResult.EOD)
					return ParseStatus.NeedMore;
				if (parsed == (long)VcDiffResult.ERROR)
					return ParseStatus.Error;

				checksum = (uint)parsed;
				i += vb;
			}
			else
			{
				if (data.Length < i + 4)
					return ParseStatus.NeedMore;

				checksum = (uint)((data[i] << 24) | (data[i + 1] << 16) | (data[i + 2] << 8) | data[i + 3]);
				i += 4;
			}
		}

		long deltaHeaderLength = i - deltaEncodingStart;
		var total = deltaHeaderLength + addRunLength + instrLength + addrLength;
		if (deltaEncodingLength != total)
			return ParseStatus.Error;

		bool interleaved;
		if (this.isSdch && addRunLength == 0 && addrLength == 0 && instrLength > 0)
			interleaved = true;
		else if (!this.isSdch || (this.isSdch && (addRunLength > 0 || addrLength > 0) && instrLength > 0))
			interleaved = false;
		else
			return ParseStatus.Error;

		this._input.Skip(i);

		this._sourceSegmentLength = sourceLength;
		this._sourceSegmentOffset = sourceOffset;
		this._targetWindowLength = targetLength;
		this._addRunLength = addRunLength;
		this._instructionAndSizesLength = instrLength;
		this._addressForCopyLength = addrLength;
		this._checksum = checksum;
		this._checksumFormat = cf;
		this._addRunCompressed = (deltaIndicator & (int)VcDiffCompressFlags.VCDDATACOMP) != 0;
		this._instructionsCompressed = (deltaIndicator & (int)VcDiffCompressFlags.VCDINSTCOMP) != 0;
		this._addressesCompressed = (deltaIndicator & (int)VcDiffCompressFlags.VCDADDRCOMP) != 0;
		this._interleavedWindow = interleaved;
		this._interleavedRemaining = interleaved ? instrLength : 0;
		this._windowHeaderParsed = true;

		this.SetupWindow();
		return ParseStatus.Ok;
	}

	private static ParseStatus ReadVarInt(ReadOnlySpan<byte> data, ref int index, out int value)
	{
		value = 0;
		var parsed = VarIntBe.ParseInt32(data.Slice(index), out var vb);
		if (parsed == (int)VcDiffResult.EOD)
			return ParseStatus.NeedMore;
		if (parsed == (int)VcDiffResult.ERROR)
			return ParseStatus.Error;

		index += vb;
		value = parsed;
		return ParseStatus.Ok;
	}

	private void SetupWindow()
	{
		if (this._targetWindow == null || this._targetWindow.Length < this._targetWindowLength)
		{
			if (this._targetWindow != null) this._pool.Return(this._targetWindow, false);
			this._targetWindow = this._pool.Rent(Math.Max(1, this._targetWindowLength));
		}

		this._targetDecoded = 0;
		this._targetEmitted = 0;
		this._runningChecksum = this._checksumFormat == ChecksumFormat.Xdelta3 ? 1u : 0u;
		this._instrPos = 0;
		this._addRunPos = 0;
		this._addrPos = 0;
		this._pendingSecondOpcode = CodeTable.KNoOpcode;
		this._hasPendingInstruction = false;
		this._pendingInstructionType = VcDiffInstructionType.NOOP;
		this._pendingInstructionSize = 0;
		this._pendingInstructionMode = 0;
		this._sectionsBuffered = false;

		Array.Clear(this._nearCache, 0, this._nearCache.Length);
		Array.Clear(this._sameCache, 0, this._sameCache.Length);
		this._nextSlot = 0;

		this.ReleaseSections();
	}

	private void FinishWindow()
	{
		this._windowHeaderParsed = false;
		this._sectionsBuffered = false;
		this._targetDecoded = 0;
		this._targetEmitted = 0;
		this._instrPos = 0;
		this._addRunPos = 0;
		this._addrPos = 0;
		this._pendingSecondOpcode = CodeTable.KNoOpcode;
		this._hasPendingInstruction = false;
		this._pendingInstructionType = VcDiffInstructionType.NOOP;
		this._pendingInstructionSize = 0;
		this._pendingInstructionMode = 0;

		this.ReleaseSections();
	}

	private void ReleaseSections()
	{
		this._addRunData.Dispose();
		this._instructionsData.Dispose();
		this._addressesData.Dispose();
		this._sectionWire.Dispose();
		this._addRunData = default;
		this._instructionsData = default;
		this._addressesData = default;
		this._sectionWire = default;
		this._sectionIndex = 0;
		this._sectionFilled = 0;
	}

	private bool VerifyChecksum()
	{
		if (this._disableChecksums || this._checksumFormat == ChecksumFormat.None)
			return true;

		var computed = this._targetDecoded == 0 ? 1u : this._runningChecksum;
		return computed == this._checksum;
	}

	// ------------------------------------------------------------------ sections

	private ParseStatus BufferSections()
	{
		// Sections are filled as the delta arrives, so the input buffer never has to hold a whole window.
		while (this._sectionIndex < 3)
		{
			int length;
			bool compressed;
			WindowSectionType sectionType;
			switch (this._sectionIndex)
			{
				case 0:
					length = (int)this._addRunLength;
					compressed = this._addRunCompressed;
					sectionType = WindowSectionType.AddRunData;
					break;
				case 1:
					length = (int)this._instructionAndSizesLength;
					compressed = this._instructionsCompressed;
					sectionType = WindowSectionType.InstructionsAndSizes;
					break;
				default:
					length = (int)this._addressForCopyLength;
					compressed = this._addressesCompressed;
					sectionType = WindowSectionType.AddressForCopy;
					break;
			}

			if (this._sectionWire.Data == null)
			{
				this._sectionWire = new PinnedArrayRental(length, this._pool);
				this._sectionFilled = 0;
			}

			var take = (int)Math.Min(length - this._sectionFilled, this._input.Available);
			if (take > 0)
			{
				this._input.Remaining.Slice(0, take).CopyTo(this._sectionWire.AsSpan().Slice(this._sectionFilled));
				this._input.Skip(take);
				this._sectionFilled += take;
			}

			if (this._sectionFilled < length)
				return ParseStatus.NeedMore;

			var data = this._sectionWire;
			this._sectionWire = default;
			if (compressed && !this.TryDecompressSection(sectionType, ref data))
				return ParseStatus.Error;

			switch (this._sectionIndex)
			{
				case 0:
					this._addRunData = data; break;
				case 1:
					this._instructionsData = data; break;
				default:
					this._addressesData = data; break;
			}

			this._sectionIndex++;
		}

		this._sectionsBuffered = true;
		return ParseStatus.Ok;
	}

	private bool TryDecompressSection(WindowSectionType sectionType, ref PinnedArrayRental data)
	{
		var wire = data;
		data = default;

		try
		{
			if (this._secondaryCompressor == null)
				return false;

			var uncompressedLength = VarIntBe.ParseInt32(wire.AsSpan(), out _);
			if (uncompressedLength < 0 || uncompressedLength > this._maxTargetWindowSize)
				return false;

			data = this._secondaryCompressor.Decompress(sectionType, wire);
			return true;
		}
		catch
		{
			return false;
		}
		finally
		{
			wire.Dispose();
		}
	}

	// ------------------------------------------------------------------ body decode

	private bool DecodeNonInterleavedInstruction(out bool needMore)
	{
		needMore = false;

		var instrSpan = this._instructionsData.AsSpanOrDefault();
		var addRunSpan = this._addRunData.AsSpanOrDefault();
		var addrSpan = this._addressesData.AsSpanOrDefault();

		var r = this.TryDecodeInstruction(instrSpan.Slice(this._instrPos), out var used, out var type, out var size, out var mode);
		if (r != VcDiffResult.SUCCESS)
			return false;

		this._instrPos += used;

		if (size > this._targetWindowLength - this._targetDecoded)
			return false;

		// ReSharper disable once SwitchStatementHandlesSomeKnownEnumValuesWithDefault
		switch (type)
		{
			case VcDiffInstructionType.ADD:
				if (this._addRunPos + size > addRunSpan.Length)
					return false;

				this.WriteTarget(addRunSpan.Slice(this._addRunPos, size));
				this._addRunPos += size;
				return true;

			case VcDiffInstructionType.RUN:
				if (this._addRunPos + 1 > addRunSpan.Length)
					return false;

				this.WriteRunTarget(addRunSpan[this._addRunPos], size);
				this._addRunPos += 1;
				return true;

			case VcDiffInstructionType.COPY:
				var here = this._sourceSegmentLength + this._targetDecoded;
				var decoded = this.DecodeAddress(here, mode, addrSpan, ref this._addrPos, out var status);
				if (status != VcDiffResult.SUCCESS)
					return false;

				return this.CopyTarget(decoded, size);

			default:
				return false;
		}
	}

	private void SkipInterleaved(int count)
	{
		this._input.Skip(count);
		this._interleavedRemaining -= count;
	}

	private bool DecodeInterleavedInstruction(out bool needMore)
	{
		needMore = false;

		VcDiffInstructionType type;
		int size;
		byte mode;
		var data = this.InterleavedData;

		// Running out of data is only recoverable while the rest of the window body is still to arrive.
		var moreToCome = data.Length < this._interleavedRemaining;

		if (this._hasPendingInstruction)
		{
			type = this._pendingInstructionType;
			size = this._pendingInstructionSize;
			mode = this._pendingInstructionMode;
		}
		else
		{
			var r = this.TryDecodeInstruction(data, out var used, out type, out size, out mode);
			if (r == VcDiffResult.EOD)
			{
				needMore = moreToCome;
				return false;
			}

			if (r != VcDiffResult.SUCCESS)
				return false;

			this.SkipInterleaved(used);
			data = data.Slice(used);

			if (size > this._targetWindowLength - this._targetDecoded)
				return false;
		}

		// ReSharper disable once SwitchStatementHandlesSomeKnownEnumValuesWithDefault
		switch (type)
		{
			case VcDiffInstructionType.ADD:
				// The data of an ADD is written as it arrives, it does not have to be buffered whole.
				var take = Math.Min(size, data.Length);
				if (take > 0)
				{
					this.WriteTarget(data.Slice(0, take));
					this.SkipInterleaved(take);
					size -= take;
				}

				if (size > 0)
				{
					this.SavePending(type, size, mode);
					needMore = moreToCome;
					return false;
				}

				this.ClearPending();
				return true;

			case VcDiffInstructionType.RUN:
				if (data.Length < 1)
				{
					this.SavePending(type, size, mode);
					needMore = moreToCome;
					return false;
				}

				this.WriteRunTarget(data[0], size);
				this.SkipInterleaved(1);
				this.ClearPending();
				return true;

			case VcDiffInstructionType.COPY:
				var here = this._sourceSegmentLength + this._targetDecoded;
				var addrIndex = 0;
				var decoded = this.DecodeAddress(here, mode, data, ref addrIndex, out var status);
				if (status == VcDiffResult.EOD)
				{
					this.SavePending(type, size, mode);
					needMore = moreToCome;
					return false;
				}

				if (status != VcDiffResult.SUCCESS)
					return false;

				this.SkipInterleaved(addrIndex);
				this.ClearPending();
				return this.CopyTarget(decoded, size);

			default:
				return false;
		}
	}

	private VcDiffResult TryDecodeInstruction(ReadOnlySpan<byte> data, out int bytesUsed, out VcDiffInstructionType type, out int size, out byte mode)
	{
		bytesUsed = 0;
		type = VcDiffInstructionType.NOOP;
		size = 0;
		mode = 0;

		var table = this._customTable?.CustomTable ?? CodeTable.DefaultTable;
		var inst1 = table.Inst1.AsSpan();
		var inst2 = table.Inst2.AsSpan();
		var size1 = table.Size1.AsSpan();
		var size2 = table.Size2.AsSpan();
		var mode1 = table.Mode1.AsSpan();
		var mode2 = table.Mode2.AsSpan();

		var pending = this._pendingSecondOpcode;
		var index = 0;
		var instructionType = CodeTable.N;
		var instructionSize = 0;
		byte instructionMode = 0;

		while (true)
		{
			if (pending != CodeTable.KNoOpcode)
			{
				var opcode = (byte)pending;
				pending = CodeTable.KNoOpcode;
				instructionType = inst2[opcode];
				instructionSize = size2[opcode];
				instructionMode = mode2[opcode];
				break;
			}

			if (index >= data.Length)
				return VcDiffResult.EOD;

			var op = data[index];
			if (inst2[op] != CodeTable.N)
				pending = op;

			index++;
			instructionType = inst1[op];
			instructionSize = size1[op];
			instructionMode = mode1[op];

			if (instructionType != CodeTable.N)
				break;
		}

		if (instructionSize == 0)
		{
			var parsed = VarIntBe.ParseInt32(data.Slice(index), out var vb);
			if (parsed == (int)VcDiffResult.ERROR)
				return VcDiffResult.ERROR;
			if (parsed == (int)VcDiffResult.EOD)
				return VcDiffResult.EOD;

			index += vb;
			size = parsed;
		}
		else
			size = instructionSize;

		mode = instructionMode;
		type = (VcDiffInstructionType)instructionType;
		bytesUsed = index;
		this._pendingSecondOpcode = pending;
		return VcDiffResult.SUCCESS;
	}

	private long DecodeAddress(long here, byte mode, ReadOnlySpan<byte> data, ref int index, out VcDiffResult status)
	{
		status = VcDiffResult.SUCCESS;
		if (here < 0)
		{
			status = VcDiffResult.ERROR;
			return 0;
		}

		long decoded;
		if (this.IsSameMode(mode))
		{
			if (index >= data.Length)
			{
				status = VcDiffResult.EOD;
				return 0;
			}

			var encoded = data[index++];
			decoded = this.SameAddress((mode - this.FirstSame) * 256 + encoded);
		}
		else
		{
			var parsed = VarIntBe.ParseInt32(data.Slice(index), out var vb);
			if (parsed == (int)VcDiffResult.ERROR)
			{
				status = VcDiffResult.ERROR;
				return 0;
			}

			if (parsed == (int)VcDiffResult.EOD)
			{
				status = VcDiffResult.EOD;
				return 0;
			}

			index += vb;
			long encoded = parsed;

			if (IsSelfMode(mode))
				decoded = encoded;
			else if (IsHereMode(mode))
				decoded = here - encoded;
			else if (this.IsNearMode(mode))
				decoded = this.NearAddress(mode - FIRST_NEAR) + encoded;
			else
			{
				status = VcDiffResult.ERROR;
				return 0;
			}
		}

		if (decoded < 0 || decoded >= here)
		{
			status = VcDiffResult.ERROR;
			return 0;
		}

		this.UpdateCache(decoded);
		return decoded;
	}

	private bool CopyTarget(long decodedAddress, int size)
	{
		var hereAddress = this._sourceSegmentLength + this._targetDecoded;
		if (decodedAddress < 0 || decodedAddress > hereAddress)
			return false;

		if (decodedAddress + size <= this._sourceSegmentLength)
		{
			this.WriteDictionaryTarget(decodedAddress + this._sourceSegmentOffset, size);
			return true;
		}

		if (decodedAddress < this._sourceSegmentLength)
		{
			var partial = (int)(this._sourceSegmentLength - decodedAddress);
			this.WriteDictionaryTarget(decodedAddress + this._sourceSegmentOffset, partial);
			size -= partial;
			decodedAddress = this._sourceSegmentLength;
		}

		decodedAddress -= this._sourceSegmentLength;
		var overlap = decodedAddress + size >= this._targetDecoded;

		if (overlap)
		{
			var availableData = (int)(this._targetDecoded - decodedAddress);
			if (availableData <= 0)
				return false;

			for (var i = 0; i < size; i += availableData)
			{
				var toCopy = Math.Min(size - i, availableData);
				this.WriteTarget(this._targetWindow.AsSpan((int)decodedAddress + i, toCopy));
			}
		}
		else
			this.WriteTarget(this._targetWindow.AsSpan((int)decodedAddress, size));

		return true;
	}

	private void WriteTarget(ReadOnlySpan<byte> data)
	{
		data.CopyTo(this._targetWindow.AsSpan(this._targetDecoded));
		this._targetDecoded += data.Length;

		if (this._checksumFormat != ChecksumFormat.None && data.Length > 0) this._runningChecksum = Adler32.Hash(this._runningChecksum, data);
	}

	private void WriteDictionaryTarget(long dictionaryOffset, int size)
	{
		var span = this._targetWindow.AsSpan(this._targetDecoded, size);
		this._dictionary.CopyTo(dictionaryOffset, span);
		this._targetDecoded += size;

		if (this._checksumFormat != ChecksumFormat.None && size > 0) this._runningChecksum = Adler32.Hash(this._runningChecksum, span);
	}

	private void WriteRunTarget(byte value, int size)
	{
		var span = this._targetWindow.AsSpan(this._targetDecoded, size);
		span.Fill(value);
		this._targetDecoded += size;

		if (this._checksumFormat != ChecksumFormat.None && size > 0) this._runningChecksum = Adler32.Hash(this._runningChecksum, span);
	}

	private int EmitTarget(Span<byte> output)
	{
		var available = this._targetDecoded - this._targetEmitted;
		var toCopy = Math.Min(available, output.Length);
		if (toCopy > 0)
		{
			this._targetWindow.AsSpan(this._targetEmitted, toCopy).CopyTo(output);
			this._targetEmitted += toCopy;
		}

		return toCopy;
	}

	private void SavePending(VcDiffInstructionType type, int size, byte mode)
	{
		this._hasPendingInstruction = true;
		this._pendingInstructionType = type;
		this._pendingInstructionSize = size;
		this._pendingInstructionMode = mode;
	}

	private void ClearPending()
	{
		this._hasPendingInstruction = false;
	}

	private static bool IsSelfMode(byte mode)
	{
		return mode == (byte)VcDiffModes.SELF;
	}

	private static bool IsHereMode(byte mode)
	{
		return mode == (byte)VcDiffModes.HERE;
	}

	private bool IsNearMode(byte mode)
	{
		return mode >= FIRST_NEAR && mode < this.FirstSame;
	}

	private bool IsSameMode(byte mode)
	{
		return mode >= this.FirstSame && mode <= this.Last;
	}

	private long NearAddress(int pos)
	{
		return this._nearCache[pos];
	}

	private long SameAddress(int pos)
	{
		return this._sameCache[pos];
	}

	private void UpdateCache(long address)
	{
		if (this._nearSize > 0)
		{
			this._nearCache[this._nextSlot] = address;
			this._nextSlot = (this._nextSlot + 1) % this._nearSize;
		}

		if (this._sameSize > 0) this._sameCache[(int)(address % (this._sameSize * 256))] = address;
	}

	/// <inheritdoc />
	public void Dispose()
	{
		if (this._disposed)
			return;

		this._disposed = true;
		this.ReleaseSections();
		this._secondaryCompressor?.Dispose();
		this._dictionary.Dispose();
		this._input.Dispose();
		if (this._targetWindow != null) this._pool.Return(this._targetWindow, false);
	}
}