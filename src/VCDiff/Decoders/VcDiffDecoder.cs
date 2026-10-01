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

	// How much unparsed delta is kept between calls before more is taken from the caller.
	private const int INPUT_BUFFER_SIZE = 16 * 1024;

	private readonly bool _allowCustomCodeTable;
	private readonly IDictionaryReader _dictionary;
	private readonly bool _disableChecksums;
	private readonly DeltaInputBuffer _input;
	private readonly int _maxTargetWindowSize;
	private readonly ArrayPool<byte> _pool;
	private AddressCache _addressCache = new();
	private bool _addressesCompressed;
	private PooledArray _addressesData;
	private long _addressForCopyLength;
	private int _addrPos;
	private bool _addRunCompressed;
	private PooledArray _addRunData;
	private long _addRunLength;
	private int _addRunPos;
	private uint _checksum;
	private ChecksumFormat _checksumFormat;
	private CustomCodeTableDecoder? _customTable;

	private bool _disposed;
	private bool _hasPendingInstruction;
	private bool _headerParsed;
	private int _instrPos;
	private long _instructionAndSizesLength;
	private bool _instructionsCompressed;
	private PooledArray _instructionsData;
	private long _interleavedRemaining;
	private bool _interleavedWindow;
	private bool _isSdch;
	private byte _pendingInstructionMode;
	private int _pendingInstructionSize;
	private VcDiffInstructionType _pendingInstructionType;
	private int _pendingSecondOpcode = CodeTable.KNoOpcode;
	private uint _runningChecksum;
	private XzSectionDecompressor? _secondaryCompressor;
	private int _sectionFilled;
	private int _sectionIndex;
	private bool _sectionsBuffered;
	private PooledArray _sectionWire;
	private long _sourceSegmentLength;
	private long _sourceSegmentOffset;
	private int _targetDecoded;
	private int _targetEmitted;
	private byte[]? _targetWindow;
	private int _targetWindowLength;
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

    /// <summary>
    ///     Creates a streaming VCDIFF decoder.
    /// </summary>
    /// <param name="dictionary">
    ///     The dictionary (source/base) data. It is referenced, not copied, and must outlive this
    ///     instance.
    /// </param>
    /// <param name="options">The decoder options. See <see cref="VcDecoderOptions" />.</param>
    public VcDiffDecoder(ReadOnlySequence<byte> dictionary, VcDecoderOptions? options = null)
		: this(ValidateOptions(options), new DictionarySource(dictionary), true)
	{
	}

    /// <summary>
    ///     Creates a decoder over any dictionary reader. The decoder takes ownership of
    ///     <paramref name="dictionary" /> and disposes it.
    /// </summary>
    /// <param name="options">Validated decoder options.</param>
    /// <param name="dictionary">The dictionary reader.</param>
    /// <param name="allowCustomCodeTable">
    ///     Whether the delta may carry a custom code table. A custom code table is itself decoded with a nested decoder
    ///     that must not accept another one (RFC 3284 section 7), which also bounds the nesting depth.
    /// </param>
    internal VcDiffDecoder(VcDecoderOptions options, IDictionaryReader dictionary, bool allowCustomCodeTable)
	{
		this._pool = options.BytePoolOrDefault;
		this._maxTargetWindowSize = options.MaxTargetFileSize;
		this._disableChecksums = options.DisableChecksums;
		this._allowCustomCodeTable = allowCustomCodeTable;
		this._dictionary = dictionary;
		this._input = new DeltaInputBuffer(8192, this._pool);
	}

    /// <summary>
    ///     Why the last <see cref="Decode" /> call returned <see cref="OperationStatus.InvalidData" />.
    /// </summary>
    internal DecodeFailure Failure { get; private set; }

    /// <summary>
    ///     Whether the delta header declared the SDCH (version 'S') format. Valid once the header has been parsed.
    /// </summary>
    internal bool IsSdchFormat => this._isSdch;

	private static VcDecoderOptions ValidateOptions(VcDecoderOptions? options)
	{
		options ??= new VcDecoderOptions();
		if (options.MaxTargetFileSize <= 0)
			throw new ArgumentOutOfRangeException(nameof(options), "MaxTargetFileSize must be positive.");

		return options;
	}

	private OperationStatus MoreOrTruncated(bool isFinal)
	{
		if (!isFinal)
			return OperationStatus.NeedMoreData;

		this.Failure = DecodeFailure.Truncated;
		return OperationStatus.InvalidData;
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
			// While a window's sections are being filled and nothing is buffered, copy section bytes
			// straight from the caller's input instead of staging them in the input buffer first.
			if (this.IsBufferingSections && this._input.Available == 0 && inputConsumed < input.Length)
			{
				var bs = this.BufferSections(input.Slice(inputConsumed), out var used);
				inputConsumed += used;
				if (bs == ParseStatus.Error)
					return this.Finish(OperationStatus.InvalidData);
			}

			// Take only as much input as fits the internal buffer, unless the decoder could
			// not make progress with a full one (e.g. a large application header).
			var room = INPUT_BUFFER_SIZE - this._input.Available;
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
				return this.Finish(status);

			stalled = this._input.Available >= INPUT_BUFFER_SIZE;
		}
	}

	private OperationStatus Finish(OperationStatus status)
	{
		if (status == OperationStatus.InvalidData && this.Failure == DecodeFailure.None)
			this.Failure = DecodeFailure.Malformed;

		return status;
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
					return this.MoreOrTruncated(isFinal);
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
					return this.MoreOrTruncated(isFinal);
				if (hs == ParseStatus.Error)
					return OperationStatus.InvalidData;
			}

			// Buffer the full delta window for non-interleaved format.
			if (this.IsBufferingSections)
			{
				var bs = this.BufferSections(this._input.Remaining, out var used);
				this._input.Skip(used);
				if (bs == ParseStatus.NeedMore)
					return this.MoreOrTruncated(isFinal);
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

			// A buffered (non-interleaved) window never stops half way for more input, so when the
			// whole of it fits it is decoded straight into the output, without staging it.
			if (!this._interleavedWindow && this._targetDecoded == 0 && output.Length - outputWritten >= this._targetWindowLength)
			{
				var window = output.Slice(outputWritten, this._targetWindowLength);
				var decodedOk = true;
				while (decodedOk && this._targetDecoded < this._targetWindowLength)
				{
					decodedOk = this.DecodeNonInterleavedInstruction(window);
				}

				// Count the bytes as emitted, exactly as the staged path would have.
				outputWritten += this._targetDecoded;
				this._targetEmitted = this._targetDecoded;
				if (!decodedOk)
					return OperationStatus.InvalidData;

				continue;
			}

			// Decode a single instruction.
			var targetWindow = this.EnsureTargetWindow();
			var needMore = false;
			var ok = this._interleavedWindow
				? this.DecodeInterleavedInstruction(targetWindow, out needMore)
				: this.DecodeNonInterleavedInstruction(targetWindow);

			if (!ok)
			{
				if (needMore)
					return this.MoreOrTruncated(isFinal);

				return OperationStatus.InvalidData;
			}
		}
	}

	// ------------------------------------------------------------------ header

	private ParseStatus ParseHeader()
	{
		var data = this._input.Remaining;
		if (data.Length < FileHeader.LENGTH)
			return ParseStatus.NeedMore;

		byte version = data[3], hdr = data[4];
		if (!data.Slice(0, FileHeader.Magic.Length).SequenceEqual(FileHeader.Magic))
			return ParseStatus.Error;
		if (version != FileHeader.VERSION_RFC3284 && version != FileHeader.VERSION_SDCH)
			return ParseStatus.Error;

		var i = FileHeader.LENGTH;

		byte secondaryId = 0;
		if ((hdr & (int)VcDiffCodeFlags.VCDDECOMPRESS) != 0)
		{
			if (data.Length <= i)
				return ParseStatus.NeedMore;

			secondaryId = data[i++];
		}

		CustomCodeTableDecoder? customTable = null;
		if ((hdr & (int)VcDiffCodeFlags.VCDCODETABLE) != 0)
		{
			if (!this._allowCustomCodeTable)
				return ParseStatus.Error;

			var len = VarIntBe.ParseInt32(data.Slice(i), out var lenBytes);
			if (len == (int)VcDiffResult.EOD)
				return ParseStatus.NeedMore;
			if (len == (int)VcDiffResult.ERROR || len <= 0)
				return ParseStatus.Error;
			if (len > data.Length - i - lenBytes)
				return ParseStatus.NeedMore;

			var ctd = new CustomCodeTableDecoder(this._pool);
			if (ctd.Decode(this._input.GetReadOnlySequence(i, lenBytes + len)) != VcDiffResult.SUCCESS)
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
			if (appLen > data.Length - i)
				return ParseStatus.NeedMore;

			i += appLen;
		}

		// xdelta3 defines 1 as "DJW static huffman" and 16 as "FGK adaptive huffman"; only 2 (LZMA) is supported.
		if (secondaryId != 0 && secondaryId != 2)
		{
			this.Failure = DecodeFailure.UnsupportedSecondaryCompressor;
			return ParseStatus.Error;
		}

		this._input.Skip(i);
		this._headerParsed = true;
		this._isSdch = version == FileHeader.VERSION_SDCH;
		this._customTable = customTable;
		if (customTable != null)
			this._addressCache = new AddressCache(customTable.NearSize, customTable.SameSize);

		if (secondaryId == 2) this._secondaryCompressor ??= new XzSectionDecompressor(this._pool);

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
		if (targetLength < 0)
			return ParseStatus.Error;
		if (targetLength > this._maxTargetWindowSize)
		{
			this.Failure = DecodeFailure.TargetWindowTooLarge;
			return ParseStatus.Error;
		}

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
			cf = this._isSdch ? ChecksumFormat.SDCH : ChecksumFormat.Xdelta3;
			if (this._isSdch)
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
		if (this._isSdch && addRunLength == 0 && addrLength == 0 && instrLength > 0)
			interleaved = true;
		else if (!this._isSdch || (this._isSdch && (addRunLength > 0 || addrLength > 0) && instrLength > 0))
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

	private bool IsBufferingSections => this._headerParsed && this._windowHeaderParsed && !this._interleavedWindow && !this._sectionsBuffered;

	// The staging buffer for the current target window. It is rented only when a window can not be
	// decoded straight into the output, and its size is bounded by MaxTargetFileSize (validated in the
	// window header).
	private byte[] EnsureTargetWindow()
	{
		if (this._targetWindow == null || this._targetWindow.Length < this._targetWindowLength)
		{
			if (this._targetWindow != null)
			{
				var old = this._targetWindow;
				this._targetWindow = null;
				this._pool.Return(old, false);
			}

			this._targetWindow = this._pool.Rent(Math.Max(1, this._targetWindowLength));
		}

		return this._targetWindow;
	}

	private void SetupWindow()
	{
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

		this._addressCache.Reset();

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

	private ParseStatus BufferSections(ReadOnlySpan<byte> source, out int consumed)
	{
		// Sections are filled as the delta arrives, so the input buffer never has to hold a whole window.
		consumed = 0;
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
				this._sectionWire = new PooledArray(length, this._pool);
				this._sectionFilled = 0;
			}

			var take = Math.Min(length - this._sectionFilled, source.Length - consumed);
			if (take > 0)
			{
				source.Slice(consumed, take).CopyTo(this._sectionWire.AsSpan().Slice(this._sectionFilled));
				consumed += take;
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

	private bool TryDecompressSection(WindowSectionType sectionType, ref PooledArray data)
	{
		var wire = data;
		data = default;

		try
		{
			if (this._secondaryCompressor == null)
				return false;

			data = this._secondaryCompressor.Decompress(sectionType, new ReadOnlySequence<byte>(wire.Data!, 0, wire.Length), this._maxTargetWindowSize);
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

	private bool DecodeNonInterleavedInstruction(Span<byte> window)
	{
		var instrSpan = this._instructionsData.AsSpan();
		var addRunSpan = this._addRunData.AsSpan();
		var addrSpan = this._addressesData.AsSpan();

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

				this.WriteTarget(window, addRunSpan.Slice(this._addRunPos, size));
				this._addRunPos += size;
				return true;

			case VcDiffInstructionType.RUN:
				if (this._addRunPos + 1 > addRunSpan.Length)
					return false;

				this.WriteRunTarget(window, addRunSpan[this._addRunPos], size);
				this._addRunPos += 1;
				return true;

			case VcDiffInstructionType.COPY:
				var here = this._sourceSegmentLength + this._targetDecoded;
				var decoded = this._addressCache.DecodeAddress(here, mode, addrSpan, ref this._addrPos, out var status);
				if (status != VcDiffResult.SUCCESS)
					return false;

				return this.CopyTarget(window, decoded, size);

			default:
				return false;
		}
	}

	private void SkipInterleaved(int count)
	{
		this._input.Skip(count);
		this._interleavedRemaining -= count;
	}

	private bool DecodeInterleavedInstruction(Span<byte> window, out bool needMore)
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
					this.WriteTarget(window, data.Slice(0, take));
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

				this.WriteRunTarget(window, data[0], size);
				this.SkipInterleaved(1);
				this.ClearPending();
				return true;

			case VcDiffInstructionType.COPY:
				var here = this._sourceSegmentLength + this._targetDecoded;
				var addrIndex = 0;
				var decoded = this._addressCache.DecodeAddress(here, mode, data, ref addrIndex, out var status);
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
				return this.CopyTarget(window, decoded, size);

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
		var inst1 = table.Inst1;
		var inst2 = table.Inst2;
		var size1 = table.Size1;
		var size2 = table.Size2;
		var mode1 = table.Mode1;
		var mode2 = table.Mode2;

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

	private bool CopyTarget(Span<byte> window, long decodedAddress, int size)
	{
		var hereAddress = this._sourceSegmentLength + this._targetDecoded;
		if (decodedAddress < 0 || decodedAddress > hereAddress)
			return false;

		if (decodedAddress + size <= this._sourceSegmentLength)
		{
			this.WriteDictionaryTarget(window, decodedAddress + this._sourceSegmentOffset, size);
			return true;
		}

		if (decodedAddress < this._sourceSegmentLength)
		{
			var partial = (int)(this._sourceSegmentLength - decodedAddress);
			this.WriteDictionaryTarget(window, decodedAddress + this._sourceSegmentOffset, partial);
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
				this.WriteTarget(window, window.Slice((int)decodedAddress + i, toCopy));
			}
		}
		else
			this.WriteTarget(window, window.Slice((int)decodedAddress, size));

		return true;
	}

	private void WriteTarget(Span<byte> window, ReadOnlySpan<byte> data)
	{
		data.CopyTo(window.Slice(this._targetDecoded));
		this._targetDecoded += data.Length;

		if (this._checksumFormat != ChecksumFormat.None && data.Length > 0) this._runningChecksum = Adler32.Hash(this._runningChecksum, data);
	}

	private void WriteDictionaryTarget(Span<byte> window, long dictionaryOffset, int size)
	{
		var span = window.Slice(this._targetDecoded, size);
		this._dictionary.CopyTo(dictionaryOffset, span);
		this._targetDecoded += size;

		if (this._checksumFormat != ChecksumFormat.None && size > 0) this._runningChecksum = Adler32.Hash(this._runningChecksum, span);
	}

	private void WriteRunTarget(Span<byte> window, byte value, int size)
	{
		var span = window.Slice(this._targetDecoded, size);
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
			this._targetWindow!.AsSpan(this._targetEmitted, toCopy).CopyTo(output);
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
		if (this._targetWindow != null)
		{
			this._pool.Return(this._targetWindow, false);
			this._targetWindow = null;
		}
	}
}

/// <summary>
///     Why a <see cref="VcDiffDecoder" /> reported <see cref="OperationStatus.InvalidData" />.
/// </summary>
internal enum DecodeFailure
{
	None,

	/// <summary>The final input ended before the delta was complete.</summary>
	Truncated,

	/// <summary>The delta is corrupt or inconsistent with the dictionary.</summary>
	Malformed,

	/// <summary>A target window is larger than <see cref="VcDecoderOptions.MaxTargetFileSize" />.</summary>
	TargetWindowTooLarge,

	/// <summary>The header names a secondary compressor other than xdelta3 LZMA (id 2).</summary>
	UnsupportedSecondaryCompressor
}
