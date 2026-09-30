using System;
using System.Buffers;
using VCDiff.Compressors;
using VCDiff.Includes;
using VCDiff.Shared;

namespace VCDiff.Decoders
{
    /// <summary>
    /// A stateful, span-based streaming VCDIFF decoder that follows the zlib-style
    /// <see cref="OperationStatus"/> contract.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Feed delta bytes via <see cref="Decode"/> and collect the decoded target bytes from the
    /// <c>output</c> span. Only a small amount of input is buffered internally, so when
    /// <see cref="OperationStatus.DestinationTooSmall"/> is returned <c>inputConsumed</c> may be less
    /// than the length of <c>input</c>. <see cref="OperationStatus.Done"/> is returned once, when
    /// <c>isFinal</c> is set and the entire delta has been decoded and emitted.
    /// </para>
    /// <para>
    /// The dictionary is not copied: its segments are pinned and read in place, so it may be made of
    /// many small buffers. The caller must keep it alive and must not mutate it until
    /// <see cref="Dispose"/> is called.
    /// </para>
    /// </remarks>
    public sealed class VcDiffDecoder : IDisposable
    {
        private const byte FirstNear = (byte)VCDiffModes.FIRST;
        private const byte DefaultNearSize = 4;
        private const byte DefaultSameSize = 3;

        // How much unparsed delta is kept between calls before more is taken from the caller.
        private const int InputBufferSize = 16 * 1024;

        private readonly DictionarySource _dictionary;
        private readonly int _maxTargetWindowSize;
        private readonly bool _disableChecksums;
        private readonly ArrayPool<byte> _pool;
        private readonly StreamingByteBuffer _input;

        // File header state.
        private bool _headerParsed;
        private bool _isSDCH;
        private byte _secondaryCompressorId;
        private CustomCodeTableDecoder? _customTable;
        private XzCompressor? _secondaryCompressor;

        // Address cache state (per window).
        private byte _nearSize = DefaultNearSize;
        private byte _sameSize = DefaultSameSize;
        private long[] _nearCache = new long[DefaultNearSize];
        private long[] _sameCache = new long[DefaultSameSize * 256];
        private int _nextSlot;

        // Window header state.
        private bool _windowHeaderParsed;
        private long _sourceSegmentLength;
        private long _sourceSegmentOffset;
        private int _targetWindowLength;
        private long _deltaEncodingLength;
        private long _addRunLength;
        private long _instructionAndSizesLength;
        private long _addressForCopyLength;
        private uint _checksum;
        private ChecksumFormat _checksumFormat;
        private bool _addRunCompressed;
        private bool _instructionsCompressed;
        private bool _addressesCompressed;
        private bool _interleavedWindow;

        // Window body state.
        private bool _sectionsBuffered;
        private int _sectionIndex;
        private int _sectionFilled;
        private PinnedArrayRental _sectionWire;
        private long _interleavedRemaining;
        private PinnedArrayRental _addRunData;
        private PinnedArrayRental _instructionsData;
        private PinnedArrayRental _addressesData;
        private int _instrPos;
        private int _addRunPos;
        private int _addrPos;

        // Target output state.
        private byte[]? _targetWindow;
        private int _targetDecoded;
        private int _targetEmitted;
        private uint _runningChecksum;

        // Instruction decode state.
        private int _pendingSecondOpcode = CodeTable.kNoOpcode;
        private bool _hasPendingInstruction;
        private VCDiffInstructionType _pendingInstructionType;
        private int _pendingInstructionSize;
        private byte _pendingInstructionMode;

        private bool _disposed;

        private enum ParseStatus
        {
            Ok,
            NeedMore,
            Error
        }

        /// <summary>
        /// Creates a streaming VCDIFF decoder.
        /// </summary>
        /// <param name="dictionary">The dictionary (source/base) data. It is referenced, not copied, and must outlive this instance.</param>
        /// <param name="options">The decoder options. See <see cref="VcDecoderOptions"/>.</param>
        public VcDiffDecoder(ReadOnlySequence<byte> dictionary, VcDecoderOptions? options = null)
        {
            options ??= new VcDecoderOptions();

            _pool = options.BytePoolOrDefault;
            _maxTargetWindowSize = options.MaxTargetFileSize;
            _disableChecksums = options.DisableChecksums;

            if (_maxTargetWindowSize <= 0)
                throw new ArgumentOutOfRangeException(nameof(options), "MaxTargetFileSize must be positive.");

            _dictionary = new DictionarySource(dictionary);
            _input = new StreamingByteBuffer(8192, _pool);
        }

        /// <summary>
        /// Decodes as much delta data as possible, writing target bytes into <paramref name="output"/>.
        /// </summary>
        /// <param name="input">The delta data to decode.</param>
        /// <param name="output">The destination for target bytes.</param>
        /// <param name="inputConsumed">The number of <paramref name="input"/> bytes consumed.</param>
        /// <param name="outputWritten">The number of bytes written to <paramref name="output"/>.</param>
        /// <param name="isFinal">Whether this is the final chunk of delta data.</param>
        /// <returns>The transformation status.</returns>
        public OperationStatus Decode(ReadOnlySpan<byte> input, Span<byte> output, out int inputConsumed, out int outputWritten, bool isFinal)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(VcDiffDecoder));

            inputConsumed = 0;
            outputWritten = 0;

            bool stalled = false;
            while (true)
            {
                // Take only as much input as fits the internal buffer, unless the decoder could
                // not make progress with a full one (e.g. a large application header).
                int room = InputBufferSize - (int)_input.Available;
                if (room <= 0 && stalled)
                    room = InputBufferSize;

                int take = Math.Min(input.Length - inputConsumed, Math.Max(room, 0));
                if (take > 0)
                {
                    _input.Append(input.Slice(inputConsumed, take));
                    inputConsumed += take;
                }

                bool final = isFinal && inputConsumed == input.Length;
                var status = DecodeCore(output.Slice(outputWritten), out int written, final);
                outputWritten += written;
                _input.Compact();

                if (status != OperationStatus.NeedMoreData || inputConsumed == input.Length)
                    return status;

                stalled = _input.Available >= InputBufferSize;
            }
        }

        private OperationStatus DecodeCore(Span<byte> output, out int outputWritten, bool isFinal)
        {
            outputWritten = 0;

            while (true)
            {
                if (!_headerParsed)
                {
                    var hs = ParseHeader();
                    if (hs == ParseStatus.NeedMore)
                        return isFinal ? OperationStatus.InvalidData : OperationStatus.NeedMoreData;
                    if (hs == ParseStatus.Error)
                        return OperationStatus.InvalidData;
                }

                // Emit any pending decoded target bytes.
                int emitted = EmitTarget(output.Slice(outputWritten));
                outputWritten += emitted;
                if (_targetEmitted < _targetDecoded)
                    return OperationStatus.DestinationTooSmall;

                // Parse the next window header.
                if (!_windowHeaderParsed)
                {
                    if (_input.Available == 0)
                        return isFinal ? OperationStatus.Done : OperationStatus.NeedMoreData;

                    var hs = ParseWindowHeader();
                    if (hs == ParseStatus.NeedMore)
                        return isFinal ? OperationStatus.InvalidData : OperationStatus.NeedMoreData;
                    if (hs == ParseStatus.Error)
                        return OperationStatus.InvalidData;
                }

                // Buffer the full delta window for non-interleaved format.
                if (!_interleavedWindow && !_sectionsBuffered)
                {
                    var bs = BufferSections();
                    if (bs == ParseStatus.NeedMore)
                        return isFinal ? OperationStatus.InvalidData : OperationStatus.NeedMoreData;
                    if (bs == ParseStatus.Error)
                        return OperationStatus.InvalidData;
                }

                // If the current window is fully decoded, finalize it and move on.
                if (_targetDecoded == _targetWindowLength)
                {
                    if (_interleavedWindow && _interleavedRemaining != 0)
                        return OperationStatus.InvalidData;
                    if (!VerifyChecksum())
                        return OperationStatus.InvalidData;

                    FinishWindow();
                    continue;
                }

                // Decode a single instruction.
                bool ok = _interleavedWindow
                    ? DecodeInterleavedInstruction(out bool needMore)
                    : DecodeNonInterleavedInstruction(out needMore);

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
            var data = _input.Remaining;
            int i = 0;

            if (data.Length < 5)
                return ParseStatus.NeedMore;

            byte V = data[0], C = data[1], D = data[2], version = data[3], hdr = data[4];
            if (V != 0xD6 || C != 0xC3 || D != 0xC4)
                return ParseStatus.Error;
            if (version != 0x00 && version != (byte)'S')
                return ParseStatus.Error;
            i = 5;

            byte secondaryId = 0;
            if ((hdr & (int)VCDiffCodeFlags.VCDDECOMPRESS) != 0)
            {
                if (data.Length < 6)
                    return ParseStatus.NeedMore;
                secondaryId = data[5];
                i = 6;
            }

            CustomCodeTableDecoder? customTable = null;
            if ((hdr & (int)VCDiffCodeFlags.VCDCODETABLE) != 0)
            {
                int len = VarIntBE.ParseInt32(data.Slice(i), out int lenBytes);
                if (len == (int)VCDiffResult.EOD)
                    return ParseStatus.NeedMore;
                if (len == (int)VCDiffResult.ERROR || len <= 0)
                    return ParseStatus.Error;
                if (i + lenBytes + len > data.Length)
                    return ParseStatus.NeedMore;

                var ctBytes = data.Slice(i, lenBytes + len).ToArray();
                using var ctb = new ByteBuffer(ctBytes);
                var ctd = new CustomCodeTableDecoder();
                if (ctd.Decode(ctb) != VCDiffResult.SUCCESS)
                    return ParseStatus.Error;
                customTable = ctd;
                i += lenBytes + len;
            }

            if ((hdr & (int)VCDiffCodeFlags.VCDAPPHEADER) != 0)
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

            _input.Skip(i);
            _headerParsed = true;
            _isSDCH = version == (byte)'S';
            _secondaryCompressorId = secondaryId;
            _customTable = customTable;
            _nearSize = customTable?.NearSize ?? DefaultNearSize;
            _sameSize = customTable?.SameSize ?? DefaultSameSize;
            _nearCache = new long[_nearSize];
            _sameCache = new long[_sameSize * 256];
            _nextSlot = 0;

            if (secondaryId == 2)
                _secondaryCompressor ??= new XzCompressor(_pool);

            return ParseStatus.Ok;
        }

        // ------------------------------------------------------------------ window header

        private ParseStatus ParseWindowHeader()
        {
            var data = _input.Remaining;
            int i = 0;

            if (data.Length < 1)
                return ParseStatus.NeedMore;

            byte winIndicator = data[i++];

            int sourceFlags = winIndicator & ((int)VCDiffWindowFlags.VCDSOURCE | (int)VCDiffWindowFlags.VCDTARGET);
            long sourceLength = 0, sourceOffset = 0;
            switch (sourceFlags)
            {
                case 0:
                    break;
                case (int)VCDiffWindowFlags.VCDSOURCE:
                    var st1 = ReadVarInt(data, ref i, out var sl);
                    if (st1 != ParseStatus.Ok) return st1;
                    sourceLength = sl;
                    var st2 = ReadVarInt(data, ref i, out var so);
                    if (st2 != ParseStatus.Ok) return st2;
                    sourceOffset = so;
                    break;
                case (int)VCDiffWindowFlags.VCDTARGET:
                    return ParseStatus.Error;
                default:
                    return ParseStatus.Error;
            }

            if (sourceLength < 0 || sourceOffset < 0 || sourceLength > _dictionary.Length || sourceOffset > _dictionary.Length || sourceOffset + sourceLength > _dictionary.Length)
                return ParseStatus.Error;

            var st3 = ReadVarInt(data, ref i, out var deltaLength);
            if (st3 != ParseStatus.Ok) return st3;
            long deltaEncodingLength = deltaLength;
            int deltaEncodingStart = i;

            var st4 = ReadVarInt(data, ref i, out var targetLength);
            if (st4 != ParseStatus.Ok) return st4;
            if (targetLength < 0 || targetLength > _maxTargetWindowSize)
                return ParseStatus.Error;

            if (i >= data.Length)
                return ParseStatus.NeedMore;
            byte deltaIndicator = data[i++];

            var st5 = ReadVarInt(data, ref i, out var addRunLength);
            if (st5 != ParseStatus.Ok) return st5;
            var st6 = ReadVarInt(data, ref i, out var instrLength);
            if (st6 != ParseStatus.Ok) return st6;
            var st7 = ReadVarInt(data, ref i, out var addrLength);
            if (st7 != ParseStatus.Ok) return st7;
            if (addRunLength < 0 || instrLength < 0 || addrLength < 0)
                return ParseStatus.Error;
            // Sections are buffered whole, so do not let a corrupt header ask for an oversized buffer.
            if (addRunLength > _maxTargetWindowSize || instrLength > _maxTargetWindowSize || addrLength > _maxTargetWindowSize)
                return ParseStatus.Error;

            uint checksum = 0;
            ChecksumFormat cf = ChecksumFormat.None;
            if ((winIndicator & (int)VCDiffWindowFlags.VCDCHECKSUM) != 0)
            {
                cf = _isSDCH ? ChecksumFormat.SDCH : ChecksumFormat.Xdelta3;
                if (_isSDCH)
                {
                    long parsed = VarIntBE.ParseInt64(data.Slice(i), out int vb);
                    if (parsed == (long)VCDiffResult.EOD)
                        return ParseStatus.NeedMore;
                    if (parsed == (long)VCDiffResult.ERROR)
                        return ParseStatus.Error;
                    checksum = (uint)parsed;
                    i += vb;
                }
                else
                {
                    if (data.Length < i + 4)
                        return ParseStatus.NeedMore;
                    checksum = (uint)(data[i] << 24 | data[i + 1] << 16 | data[i + 2] << 8 | data[i + 3]);
                    i += 4;
                }
            }

            long deltaHeaderLength = i - deltaEncodingStart;
            long total = deltaHeaderLength + addRunLength + instrLength + addrLength;
            if (deltaEncodingLength != total)
                return ParseStatus.Error;

            bool interleaved;
            if (_isSDCH && addRunLength == 0 && addrLength == 0 && instrLength > 0)
                interleaved = true;
            else if (!_isSDCH || (_isSDCH && (addRunLength > 0 || addrLength > 0) && instrLength > 0))
                interleaved = false;
            else
                return ParseStatus.Error;

            _input.Skip(i);

            _sourceSegmentLength = sourceLength;
            _sourceSegmentOffset = sourceOffset;
            _targetWindowLength = targetLength;
            _deltaEncodingLength = deltaEncodingLength;
            _addRunLength = addRunLength;
            _instructionAndSizesLength = instrLength;
            _addressForCopyLength = addrLength;
            _checksum = checksum;
            _checksumFormat = cf;
            _addRunCompressed = (deltaIndicator & (int)VCDiffCompressFlags.VCDDATACOMP) != 0;
            _instructionsCompressed = (deltaIndicator & (int)VCDiffCompressFlags.VCDINSTCOMP) != 0;
            _addressesCompressed = (deltaIndicator & (int)VCDiffCompressFlags.VCDADDRCOMP) != 0;
            _interleavedWindow = interleaved;
            _interleavedRemaining = interleaved ? instrLength : 0;
            _windowHeaderParsed = true;

            SetupWindow();
            return ParseStatus.Ok;
        }

        private static ParseStatus ReadVarInt(ReadOnlySpan<byte> data, ref int index, out int value)
        {
            value = 0;
            int parsed = VarIntBE.ParseInt32(data.Slice(index), out int vb);
            if (parsed == (int)VCDiffResult.EOD)
                return ParseStatus.NeedMore;
            if (parsed == (int)VCDiffResult.ERROR)
                return ParseStatus.Error;

            index += vb;
            value = parsed;
            return ParseStatus.Ok;
        }

        private void SetupWindow()
        {
            if (_targetWindow == null || _targetWindow.Length < _targetWindowLength)
            {
                if (_targetWindow != null)
                    _pool.Return(_targetWindow, false);
                _targetWindow = _pool.Rent(Math.Max(1, _targetWindowLength));
            }

            _targetDecoded = 0;
            _targetEmitted = 0;
            _runningChecksum = _checksumFormat == ChecksumFormat.Xdelta3 ? 1u : 0u;
            _instrPos = 0;
            _addRunPos = 0;
            _addrPos = 0;
            _pendingSecondOpcode = CodeTable.kNoOpcode;
            _hasPendingInstruction = false;
            _pendingInstructionType = VCDiffInstructionType.NOOP;
            _pendingInstructionSize = 0;
            _pendingInstructionMode = 0;
            _sectionsBuffered = false;

            Array.Clear(_nearCache, 0, _nearCache.Length);
            Array.Clear(_sameCache, 0, _sameCache.Length);
            _nextSlot = 0;

            ReleaseSections();
        }

        private void FinishWindow()
        {
            _windowHeaderParsed = false;
            _sectionsBuffered = false;
            _targetDecoded = 0;
            _targetEmitted = 0;
            _instrPos = 0;
            _addRunPos = 0;
            _addrPos = 0;
            _pendingSecondOpcode = CodeTable.kNoOpcode;
            _hasPendingInstruction = false;
            _pendingInstructionType = VCDiffInstructionType.NOOP;
            _pendingInstructionSize = 0;
            _pendingInstructionMode = 0;

            ReleaseSections();
        }

        private void ReleaseSections()
        {
            _addRunData.Dispose();
            _instructionsData.Dispose();
            _addressesData.Dispose();
            _sectionWire.Dispose();
            _addRunData = default;
            _instructionsData = default;
            _addressesData = default;
            _sectionWire = default;
            _sectionIndex = 0;
            _sectionFilled = 0;
        }

        private bool VerifyChecksum()
        {
            if (_disableChecksums || _checksumFormat == ChecksumFormat.None)
                return true;

            uint computed = _targetDecoded == 0 ? 1u : _runningChecksum;
            return computed == _checksum;
        }

        // ------------------------------------------------------------------ sections

        private ParseStatus BufferSections()
        {
            // Sections are filled as the delta arrives, so the input buffer never has to hold a whole window.
            while (_sectionIndex < 3)
            {
                int length;
                bool compressed;
                WindowSectionType sectionType;
                switch (_sectionIndex)
                {
                    case 0:
                        length = (int)_addRunLength;
                        compressed = _addRunCompressed;
                        sectionType = WindowSectionType.AddRunData;
                        break;
                    case 1:
                        length = (int)_instructionAndSizesLength;
                        compressed = _instructionsCompressed;
                        sectionType = WindowSectionType.InstructionsAndSizes;
                        break;
                    default:
                        length = (int)_addressForCopyLength;
                        compressed = _addressesCompressed;
                        sectionType = WindowSectionType.AddressForCopy;
                        break;
                }

                if (_sectionWire.Data == null)
                {
                    _sectionWire = new PinnedArrayRental(length, _pool);
                    _sectionFilled = 0;
                }

                int take = (int)Math.Min(length - _sectionFilled, _input.Available);
                if (take > 0)
                {
                    _input.Remaining.Slice(0, take).CopyTo(_sectionWire.AsSpan().Slice(_sectionFilled));
                    _input.Skip(take);
                    _sectionFilled += take;
                }

                if (_sectionFilled < length)
                    return ParseStatus.NeedMore;

                var data = _sectionWire;
                _sectionWire = default;
                if (compressed && !TryDecompressSection(sectionType, ref data))
                    return ParseStatus.Error;

                switch (_sectionIndex)
                {
                    case 0: _addRunData = data; break;
                    case 1: _instructionsData = data; break;
                    default: _addressesData = data; break;
                }

                _sectionIndex++;
            }

            _sectionsBuffered = true;
            return ParseStatus.Ok;
        }

        private bool TryDecompressSection(WindowSectionType sectionType, ref PinnedArrayRental data)
        {
            var wire = data;
            data = default;

            try
            {
                if (_secondaryCompressor == null)
                    return false;

                int uncompressedLength = VarIntBE.ParseInt32(wire.AsSpan(), out _);
                if (uncompressedLength < 0 || uncompressedLength > _maxTargetWindowSize)
                    return false;

                data = _secondaryCompressor.Decompress(sectionType, wire);
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

            var instrSpan = _instructionsData.AsSpanOrDefault();
            var addRunSpan = _addRunData.AsSpanOrDefault();
            var addrSpan = _addressesData.AsSpanOrDefault();

            var r = TryDecodeInstruction(instrSpan.Slice(_instrPos), out int used, out var type, out int size, out byte mode);
            if (r != VCDiffResult.SUCCESS)
                return false;

            _instrPos += used;

            if (size > _targetWindowLength - _targetDecoded)
                return false;

            switch (type)
            {
                case VCDiffInstructionType.ADD:
                    if (_addRunPos + size > addRunSpan.Length)
                        return false;
                    WriteTarget(addRunSpan.Slice(_addRunPos, size));
                    _addRunPos += size;
                    return true;

                case VCDiffInstructionType.RUN:
                    if (_addRunPos + 1 > addRunSpan.Length)
                        return false;
                    WriteRunTarget(addRunSpan[_addRunPos], size);
                    _addRunPos += 1;
                    return true;

                case VCDiffInstructionType.COPY:
                    long here = _sourceSegmentLength + _targetDecoded;
                    long decoded = DecodeAddress(here, mode, addrSpan, ref _addrPos, out var status);
                    if (status != VCDiffResult.SUCCESS)
                        return false;
                    return CopyTarget(decoded, size);

                default:
                    return false;
            }
        }

        // The unread part of the interleaved window body that has arrived so far.
        private ReadOnlySpan<byte> InterleavedData
        {
            get
            {
                var data = _input.Remaining;
                return data.Length > _interleavedRemaining ? data.Slice(0, (int)_interleavedRemaining) : data;
            }
        }

        private void SkipInterleaved(int count)
        {
            _input.Skip(count);
            _interleavedRemaining -= count;
        }

        private bool DecodeInterleavedInstruction(out bool needMore)
        {
            needMore = false;

            VCDiffInstructionType type;
            int size;
            byte mode;
            var data = InterleavedData;
            // Running out of data is only recoverable while the rest of the window body is still to arrive.
            bool moreToCome = data.Length < _interleavedRemaining;

            if (_hasPendingInstruction)
            {
                type = _pendingInstructionType;
                size = _pendingInstructionSize;
                mode = _pendingInstructionMode;
            }
            else
            {
                var r = TryDecodeInstruction(data, out int used, out type, out size, out mode);
                if (r == VCDiffResult.EOD)
                {
                    needMore = moreToCome;
                    return false;
                }
                if (r != VCDiffResult.SUCCESS)
                    return false;

                SkipInterleaved(used);
                data = data.Slice(used);

                if (size > _targetWindowLength - _targetDecoded)
                    return false;
            }

            switch (type)
            {
                case VCDiffInstructionType.ADD:
                    // The data of an ADD is written as it arrives, it does not have to be buffered whole.
                    int take = Math.Min(size, data.Length);
                    if (take > 0)
                    {
                        WriteTarget(data.Slice(0, take));
                        SkipInterleaved(take);
                        size -= take;
                    }

                    if (size > 0)
                    {
                        SavePending(type, size, mode);
                        needMore = moreToCome;
                        return false;
                    }

                    ClearPending();
                    return true;

                case VCDiffInstructionType.RUN:
                    if (data.Length < 1)
                    {
                        SavePending(type, size, mode);
                        needMore = moreToCome;
                        return false;
                    }
                    WriteRunTarget(data[0], size);
                    SkipInterleaved(1);
                    ClearPending();
                    return true;

                case VCDiffInstructionType.COPY:
                    long here = _sourceSegmentLength + _targetDecoded;
                    int addrIndex = 0;
                    long decoded = DecodeAddress(here, mode, data, ref addrIndex, out var status);
                    if (status == VCDiffResult.EOD)
                    {
                        SavePending(type, size, mode);
                        needMore = moreToCome;
                        return false;
                    }
                    if (status != VCDiffResult.SUCCESS)
                        return false;

                    SkipInterleaved(addrIndex);
                    ClearPending();
                    return CopyTarget(decoded, size);

                default:
                    return false;
            }
        }

        private VCDiffResult TryDecodeInstruction(ReadOnlySpan<byte> data, out int bytesUsed, out VCDiffInstructionType type, out int size, out byte mode)
        {
            bytesUsed = 0;
            type = VCDiffInstructionType.NOOP;
            size = 0;
            mode = 0;

            var table = _customTable?.CustomTable ?? CodeTable.DefaultTable;
            var inst1 = table.inst1.AsSpan();
            var inst2 = table.inst2.AsSpan();
            var size1 = table.size1.AsSpan();
            var size2 = table.size2.AsSpan();
            var mode1 = table.mode1.AsSpan();
            var mode2 = table.mode2.AsSpan();

            int pending = _pendingSecondOpcode;
            int index = 0;
            byte instructionType = CodeTable.N;
            int instructionSize = 0;
            byte instructionMode = 0;

            while (true)
            {
                if (pending != CodeTable.kNoOpcode)
                {
                    byte opcode = (byte)pending;
                    pending = CodeTable.kNoOpcode;
                    instructionType = inst2[opcode];
                    instructionSize = size2[opcode];
                    instructionMode = mode2[opcode];
                    break;
                }

                if (index >= data.Length)
                    return VCDiffResult.EOD;

                byte op = data[index];
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
                int parsed = VarIntBE.ParseInt32(data.Slice(index), out int vb);
                if (parsed == (int)VCDiffResult.ERROR)
                    return VCDiffResult.ERROR;
                if (parsed == (int)VCDiffResult.EOD)
                    return VCDiffResult.EOD;

                index += vb;
                size = parsed;
            }
            else
            {
                size = instructionSize;
            }

            mode = instructionMode;
            type = (VCDiffInstructionType)instructionType;
            bytesUsed = index;
            _pendingSecondOpcode = pending;
            return VCDiffResult.SUCCESS;
        }

        private long DecodeAddress(long here, byte mode, ReadOnlySpan<byte> data, ref int index, out VCDiffResult status)
        {
            status = VCDiffResult.SUCCESS;
            if (here < 0)
            {
                status = VCDiffResult.ERROR;
                return 0;
            }

            long decoded;
            if (IsSameMode(mode))
            {
                if (index >= data.Length)
                {
                    status = VCDiffResult.EOD;
                    return 0;
                }

                byte encoded = data[index++];
                decoded = SameAddress(((mode - FirstSame) * 256) + encoded);
            }
            else
            {
                int parsed = VarIntBE.ParseInt32(data.Slice(index), out int vb);
                if (parsed == (int)VCDiffResult.ERROR)
                {
                    status = VCDiffResult.ERROR;
                    return 0;
                }
                if (parsed == (int)VCDiffResult.EOD)
                {
                    status = VCDiffResult.EOD;
                    return 0;
                }

                index += vb;
                long encoded = parsed;

                if (IsSelfMode(mode))
                    decoded = encoded;
                else if (IsHereMode(mode))
                    decoded = here - encoded;
                else if (IsNearMode(mode))
                    decoded = NearAddress(mode - FirstNear) + encoded;
                else
                {
                    status = VCDiffResult.ERROR;
                    return 0;
                }
            }

            if (decoded < 0 || decoded >= here)
            {
                status = VCDiffResult.ERROR;
                return 0;
            }

            UpdateCache(decoded);
            return decoded;
        }

        private bool CopyTarget(long decodedAddress, int size)
        {
            long hereAddress = _sourceSegmentLength + _targetDecoded;
            if (decodedAddress < 0 || decodedAddress > hereAddress)
                return false;

            if (decodedAddress + size <= _sourceSegmentLength)
            {
                WriteDictionaryTarget(decodedAddress + _sourceSegmentOffset, size);
                return true;
            }

            if (decodedAddress < _sourceSegmentLength)
            {
                int partial = (int)(_sourceSegmentLength - decodedAddress);
                WriteDictionaryTarget(decodedAddress + _sourceSegmentOffset, partial);
                size -= partial;
                decodedAddress = _sourceSegmentLength;
            }

            decodedAddress -= _sourceSegmentLength;
            bool overlap = decodedAddress + size >= _targetDecoded;

            if (overlap)
            {
                int availableData = (int)(_targetDecoded - decodedAddress);
                if (availableData <= 0)
                    return false;

                for (int i = 0; i < size; i += availableData)
                {
                    int toCopy = Math.Min(size - i, availableData);
                    WriteTarget(_targetWindow.AsSpan((int)decodedAddress + i, toCopy));
                }
            }
            else
            {
                WriteTarget(_targetWindow.AsSpan((int)decodedAddress, size));
            }

            return true;
        }

        private void WriteTarget(ReadOnlySpan<byte> data)
        {
            data.CopyTo(_targetWindow.AsSpan(_targetDecoded));
            _targetDecoded += data.Length;

            if (_checksumFormat != ChecksumFormat.None && data.Length > 0)
                _runningChecksum = Adler32.Hash(_runningChecksum, data);
        }

        private void WriteDictionaryTarget(long dictionaryOffset, int size)
        {
            var span = _targetWindow.AsSpan(_targetDecoded, size);
            _dictionary.CopyTo(dictionaryOffset, span);
            _targetDecoded += size;

            if (_checksumFormat != ChecksumFormat.None && size > 0)
                _runningChecksum = Adler32.Hash(_runningChecksum, span);
        }

        private void WriteRunTarget(byte value, int size)
        {
            var span = _targetWindow.AsSpan(_targetDecoded, size);
            span.Fill(value);
            _targetDecoded += size;

            if (_checksumFormat != ChecksumFormat.None && size > 0)
                _runningChecksum = Adler32.Hash(_runningChecksum, span);
        }

        private int EmitTarget(Span<byte> output)
        {
            int available = _targetDecoded - _targetEmitted;
            int toCopy = Math.Min(available, output.Length);
            if (toCopy > 0)
            {
                _targetWindow.AsSpan(_targetEmitted, toCopy).CopyTo(output);
                _targetEmitted += toCopy;
            }

            return toCopy;
        }

        private void SavePending(VCDiffInstructionType type, int size, byte mode)
        {
            _hasPendingInstruction = true;
            _pendingInstructionType = type;
            _pendingInstructionSize = size;
            _pendingInstructionMode = mode;
        }

        private void ClearPending() => _hasPendingInstruction = false;

        // ------------------------------------------------------------------ address cache

        private byte FirstSame => (byte)(VCDiffModes.FIRST + _nearSize);

        private byte Last => (byte)(FirstSame + _sameSize - 1);

        private static bool IsSelfMode(byte mode) => mode == (byte)VCDiffModes.SELF;

        private static bool IsHereMode(byte mode) => mode == (byte)VCDiffModes.HERE;

        private bool IsNearMode(byte mode) => mode >= FirstNear && mode < FirstSame;

        private bool IsSameMode(byte mode) => mode >= FirstSame && mode <= Last;

        private long NearAddress(int pos) => _nearCache[pos];

        private long SameAddress(int pos) => _sameCache[pos];

        private void UpdateCache(long address)
        {
            if (_nearSize > 0)
            {
                _nearCache[_nextSlot] = address;
                _nextSlot = (_nextSlot + 1) % _nearSize;
            }

            if (_sameSize > 0)
                _sameCache[(int)(address % (_sameSize * 256))] = address;
        }

        /// <inheritdoc />
        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            ReleaseSections();
            _secondaryCompressor?.Dispose();
            _dictionary.Dispose();
            _input.Dispose();
            if (_targetWindow != null)
                _pool.Return(_targetWindow, false);
        }
    }
}
