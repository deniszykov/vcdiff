using System;
using System.Buffers;
using System.Buffers.Binary;
using System.IO;
using VCDiff.Compression.LZMA.LZ;

namespace VCDiff.Compression.LZMA
{
    /// <summary>
    /// Minimal decode-only LZMA / LZMA2 stream, adapted from SharpCompress for use by the
    /// vendored XZ decoder. Encoding support has been removed.
    /// </summary>
    internal class LzmaStream : Stream
    {
        private readonly Stream? _inputStream;
        private readonly long _inputSize;
        private readonly long _outputSize;
        private readonly bool _leaveOpen;

        private readonly int _dictionarySize;
        private readonly OutWindow _outWindow;
        private readonly RangeCoder.Decoder _rangeDecoder;
        private Decoder? _decoder;

        private long _position;
        private bool _endReached;
        private long _availableBytes;
        private long _rangeDecoderLimit;
        private long _inputPosition;

        // LZMA2
        private readonly bool _isLzma2;
        private bool _uncompressedChunk;
        private bool _needDictReset = true;
        private bool _needProps = true;

        private bool _isDisposed;

        private LzmaStream(
            byte[] properties,
            Stream inputStream,
            long inputSize,
            long outputSize,
            bool isLzma2,
            bool leaveOpen = false,
            ArrayPool<byte>? bytePool = null
        )
        {
            _outWindow = new OutWindow(bytePool);
            _rangeDecoder = new RangeCoder.Decoder(bytePool);
            _inputStream = inputStream;
            _inputSize = inputSize;
            _outputSize = outputSize;
            _isLzma2 = isLzma2;
            _leaveOpen = leaveOpen;
            if (!isLzma2)
            {
                _dictionarySize = BinaryPrimitives.ReadInt32LittleEndian(properties.AsSpan(1));
                _outWindow.Create(_dictionarySize);

                _decoder = new Decoder();
                _decoder.SetDecoderProperties(properties);
                Properties = properties;

                _availableBytes = outputSize < 0 ? long.MaxValue : outputSize;
                _rangeDecoderLimit = inputSize;
            }
            else
            {
                _dictionarySize = 2 | (properties[0] & 1);
                _dictionarySize <<= (properties[0] >> 1) + 11;

                _outWindow.Create(_dictionarySize);

                Properties = new byte[1];
                _availableBytes = 0;
            }
        }

        public static LzmaStream Create(
            byte[] properties,
            Stream inputStream,
            ArrayPool<byte>? bytePool = null,
            bool leaveOpen = false
        ) => Create(properties, inputStream, -1, -1, null, properties.Length < 5, leaveOpen, bytePool);

        public static LzmaStream Create(
            byte[] properties,
            Stream inputStream,
            long inputSize,
            bool leaveOpen = false,
            ArrayPool<byte>? bytePool = null
        ) => Create(properties, inputStream, inputSize, -1, null, properties.Length < 5, leaveOpen, bytePool);

        public static LzmaStream Create(
            byte[] properties,
            Stream inputStream,
            long inputSize,
            long outputSize,
            bool leaveOpen = false,
            ArrayPool<byte>? bytePool = null
        ) =>
            Create(
                properties,
                inputStream,
                inputSize,
                outputSize,
                null,
                properties.Length < 5,
                leaveOpen,
                bytePool
            );

        private static LzmaStream Create(
            byte[] properties,
            Stream inputStream,
            long inputSize,
            long outputSize,
            Stream? presetDictionary,
            bool isLzma2,
            bool leaveOpen = false,
            ArrayPool<byte>? bytePool = null
        )
        {
            var lzma = new LzmaStream(
                properties,
                inputStream,
                inputSize,
                outputSize,
                isLzma2,
                leaveOpen,
                bytePool
            );
            if (!isLzma2)
            {
                if (presetDictionary != null)
                {
                    lzma._outWindow.Train(presetDictionary);
                }

                lzma._rangeDecoder.Init(inputStream);
                lzma._rangeDecoder.SetFastLimit(lzma._rangeDecoderLimit);
            }
            else
            {
                if (presetDictionary != null)
                {
                    lzma._outWindow.Train(presetDictionary);
                    lzma._needDictReset = false;
                }
            }
            return lzma;
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override void Flush() { }

        protected override void Dispose(bool disposing)
        {
            if (_isDisposed)
            {
                return;
            }
            _isDisposed = true;
            if (disposing)
            {
                if (!_leaveOpen)
                {
                    _inputStream?.Dispose();
                }
                _outWindow.Dispose();
            }
            base.Dispose(disposing);
        }

        public override long Length => _position + _availableBytes;

        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_endReached)
            {
                return 0;
            }

            var total = 0;
            while (total < count)
            {
                if (_availableBytes == 0)
                {
                    if (_isLzma2)
                    {
                        DecodeChunkHeader();
                    }
                    else
                    {
                        _endReached = true;
                    }
                    if (_endReached)
                    {
                        break;
                    }
                }

                var toProcess = count - total;
                if (toProcess > _availableBytes)
                {
                    toProcess = (int)_availableBytes;
                }

                _outWindow.SetLimit(toProcess);
                if (_uncompressedChunk)
                {
                    _inputPosition += _outWindow.CopyStream(_inputStream!, toProcess);
                }
                else if (_decoder!.Code(_dictionarySize, _outWindow, _rangeDecoder))
                {
                    HandleEndMarker();
                }

                var read = _outWindow.Read(buffer, offset, toProcess);
                total += read;
                offset += read;
                _position += read;
                _availableBytes -= read;

                if (_availableBytes == 0 && !_uncompressedChunk)
                {
                    if (_isLzma2 && _decoder!.HasEndMarker)
                    {
                        throw new DataErrorException();
                    }

                    // Check range corruption scenario
                    if (
                        !_rangeDecoder.IsFinished
                        || (_rangeDecoderLimit >= 0 && _rangeDecoder._total != _rangeDecoderLimit)
                    )
                    {
                        // Stream might have End Of Stream marker
                        _outWindow.SetLimit(toProcess + 1);
                        if (!_decoder!.Code(_dictionarySize, _outWindow, _rangeDecoder))
                        {
                            _rangeDecoder.ReleaseStream();
                            throw new DataErrorException();
                        }
                    }

                    _rangeDecoder.ReleaseStream();

                    _inputPosition += _rangeDecoder._total;
                    if (_outWindow.HasPending)
                    {
                        throw new DataErrorException();
                    }
                }
            }

            if (_endReached)
            {
                if (_inputSize >= 0 && _inputPosition != _inputSize)
                {
                    throw new DataErrorException();
                }
                if (_outputSize >= 0 && _position != _outputSize)
                {
                    throw new DataErrorException();
                }
            }

            return total;
        }

        public override int ReadByte()
        {
            if (_endReached)
            {
                return -1;
            }

            if (_availableBytes == 0)
            {
                if (_isLzma2)
                {
                    DecodeChunkHeader();
                }
                else
                {
                    _endReached = true;
                }
            }

            if (_endReached)
            {
                if (_inputSize >= 0 && _inputPosition != _inputSize)
                {
                    throw new DataErrorException();
                }
                if (_outputSize >= 0 && _position != _outputSize)
                {
                    throw new DataErrorException();
                }

                return -1;
            }

            _outWindow.SetLimit(1);
            if (_uncompressedChunk)
            {
                _inputPosition += _outWindow.CopyStream(_inputStream!, 1);
            }
            else if (_decoder!.Code(_dictionarySize, _outWindow, _rangeDecoder))
            {
                HandleEndMarker();
            }

            var value = _outWindow.ReadByte();
            _position++;
            _availableBytes--;

            if (_availableBytes == 0 && !_uncompressedChunk)
            {
                if (_isLzma2 && _decoder!.HasEndMarker)
                {
                    throw new DataErrorException();
                }

                // Check range corruption scenario
                if (
                    !_rangeDecoder.IsFinished
                    || (_rangeDecoderLimit >= 0 && _rangeDecoder._total != _rangeDecoderLimit)
                )
                {
                    // Stream might have End Of Stream marker
                    _outWindow.SetLimit(2);
                    if (!_decoder!.Code(_dictionarySize, _outWindow, _rangeDecoder))
                    {
                        _rangeDecoder.ReleaseStream();
                        throw new DataErrorException();
                    }
                }

                _rangeDecoder.ReleaseStream();

                _inputPosition += _rangeDecoder._total;
                if (_outWindow.HasPending)
                {
                    throw new DataErrorException();
                }
            }

            return value;
        }

        private void DecodeChunkHeader()
        {
            var control = _inputStream!.ReadByte();
            _inputPosition++;

            if (control == 0x00)
            {
                if (_isLzma2 && _decoder is { HasEndMarker: true })
                {
                    throw new DataErrorException();
                }

                _endReached = true;
                return;
            }

            if (control >= 0xE0 || control == 0x01)
            {
                _needProps = true;
                _needDictReset = false;
                _outWindow.Reset();
            }
            else if (_needDictReset)
            {
                throw new DataErrorException();
            }

            if (control >= 0x80)
            {
                _uncompressedChunk = false;

                _availableBytes = (control & 0x1F) << 16;
                _availableBytes += (_inputStream.ReadByte() << 8) + _inputStream.ReadByte() + 1;
                _inputPosition += 2;

                _rangeDecoderLimit = (_inputStream.ReadByte() << 8) + _inputStream.ReadByte() + 1;
                _inputPosition += 2;

                if (control >= 0xC0)
                {
                    _needProps = false;
                    Properties[0] = (byte)_inputStream.ReadByte();
                    _inputPosition++;

                    _decoder = new Decoder();
                    _decoder.SetDecoderProperties(Properties);
                }
                else if (_needProps)
                {
                    throw new DataErrorException();
                }
                else if (control >= 0xA0)
                {
                    _decoder = new Decoder();
                    _decoder.SetDecoderProperties(Properties);
                }

                _rangeDecoder.Init(_inputStream);
                _rangeDecoder.SetFastLimit(_rangeDecoderLimit);
            }
            else if (control > 0x02)
            {
                throw new DataErrorException();
            }
            else
            {
                _uncompressedChunk = true;
                _availableBytes = (_inputStream.ReadByte() << 8) + _inputStream.ReadByte() + 1;
                _inputPosition += 2;
            }
        }

        private void HandleEndMarker()
        {
            if (_isLzma2)
            {
                throw new DataErrorException();
            }

            if (_outputSize < 0)
            {
                _availableBytes = _outWindow.AvailableBytes;
            }
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public byte[] Properties { get; } = new byte[5];
    }
}
