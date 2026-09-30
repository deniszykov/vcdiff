using System;
using System.Buffers;
using Microsoft.IO;
using VCDiff.Shared;

namespace VCDiff.Encoders
{
    /// <summary>
    /// A stateful, span-based streaming VCDIFF encoder that follows the zlib-style
    /// <see cref="OperationStatus"/> contract.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The encoder is a block algorithm: the VCDIFF format is window based, so the encoder
    /// accumulates target bytes until a full window is available, then encodes and emits that
    /// window. <see cref="OperationStatus.Done"/> is returned at each window boundary, and again
    /// once the final window has been flushed after <c>isFinal</c> is set.
    /// </para>
    /// <para>
    /// The dictionary is not copied: its segments are pinned and read in place, so it may be made of
    /// many small buffers. The memory behind it must stay alive and unchanged until
    /// <see cref="Dispose"/> is called.
    /// </para>
    /// </remarks>
    public sealed class VcDiffEncoder : IDisposable
    {
        private static readonly byte[] MagicBytes = { 0xD6, 0xC3, 0xC4, 0x00, 0x00 };
        private static readonly byte[] MagicBytesExtended = { 0xD6, 0xC3, 0xC4, (byte)'S', 0x00 };

        private readonly DictionarySource _dictionary;
        private readonly ArrayPool<byte> _pool;
        private readonly ChunkEncoder _chunker;
        private readonly RollingHash _hasher;
        private readonly bool _ownsHasher;
        private readonly int _windowSize;

        private byte[] _targetWindow;
        private int _targetLength;

        private readonly RecyclableMemoryStream _pending;
        private long _pendingReadOffset;

        private bool _finished;
        private bool _disposed;

        /// <summary>
        /// Creates a streaming VCDIFF encoder.
        /// </summary>
        /// <param name="dictionary">The dictionary (source/base) data. It is referenced, not copied, and must outlive this instance.</param>
        /// <param name="options">The encoder options. See <see cref="VcEncoderOptions"/>.</param>
        public VcDiffEncoder(ReadOnlySequence<byte> dictionary, VcEncoderOptions? options = null)
        {
            options ??= new VcEncoderOptions();
            _pool = options.BytePoolOrDefault;

            bool interleaved = options.Interleaved;
            var checksumFormat = options.ChecksumFormat;
            if (interleaved && checksumFormat == ChecksumFormat.Xdelta3)
                throw new ArgumentException("Interleaved diffs can not have an xdelta3 checksum!");

            int maxWindowSize = options.MaxBufferSize;
            if (maxWindowSize <= 0)
                maxWindowSize = 1;
            _windowSize = maxWindowSize * 1024 * 1024;

            int blockSize = options.BlockSize;
            int chunkSize = options.ChunkSize < 2 ? blockSize * 2 : options.ChunkSize;
            if (blockSize % 2 != 0 || chunkSize < 2 || chunkSize < 2 * blockSize)
                throw new ArgumentException($"{blockSize} can not be less than 2 or twice the blocksize of the dictionary {blockSize}.");

            var rollingHash = options.RollingHash;
            if (rollingHash == null)
            {
                _hasher = new RollingHash(blockSize);
                _ownsHasher = true;
            }
            else
            {
                _hasher = rollingHash;
                _ownsHasher = false;
            }

            if (_hasher.WindowSize != blockSize)
                throw new ArgumentException("Supplied RollingHash instance has a different window size than blocksize!");

            _dictionary = new DictionarySource(dictionary);
            var blockHash = new BlockHash(_dictionary, _hasher, blockSize);
            blockHash.AddAllBlocks();

            _chunker = new ChunkEncoder(blockHash, _dictionary.Length, _hasher, checksumFormat, interleaved, chunkSize);

            _targetWindow = _pool.Rent(_windowSize);
            _pending = Pool.MemoryStreamManager.GetStream(nameof(VcDiffEncoder));

            var magic = (!interleaved && checksumFormat != ChecksumFormat.SDCH) ? MagicBytes : MagicBytesExtended;
            _pending.Write(magic.AsSpan());
        }

        /// <summary>
        /// Encodes as much target data as possible, writing delta bytes into <paramref name="output"/>.
        /// </summary>
        /// <param name="input">The target data to encode.</param>
        /// <param name="output">The destination for delta bytes.</param>
        /// <param name="inputConsumed">The number of <paramref name="input"/> bytes consumed.</param>
        /// <param name="outputWritten">The number of bytes written to <paramref name="output"/>.</param>
        /// <param name="isFinal">Whether this is the final chunk of target data.</param>
        /// <returns>The transformation status.</returns>
        public OperationStatus Encode(ReadOnlySpan<byte> input, Span<byte> output, out int inputConsumed, out int outputWritten, bool isFinal)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(VcDiffEncoder));

            inputConsumed = 0;
            outputWritten = 0;

            if (_finished)
                return OperationStatus.Done;

            // Drain any pending encoded output first.
            outputWritten = DrainPending(output);
            if (_pendingReadOffset < _pending.Length)
                return OperationStatus.DestinationTooSmall;

            // Accumulate target input into the current window.
            while (inputConsumed < input.Length)
            {
                int space = _windowSize - _targetLength;
                int take = Math.Min(input.Length - inputConsumed, space);
                input.Slice(inputConsumed, take).CopyTo(_targetWindow.AsSpan(_targetLength, take));
                _targetLength += take;
                inputConsumed += take;

                if (_targetLength == _windowSize)
                {
                    EncodeWindow();
                    outputWritten += DrainPending(output.Slice(outputWritten));
                    if (_pendingReadOffset < _pending.Length)
                        return OperationStatus.DestinationTooSmall;
                    return OperationStatus.Done;
                }
            }

            if (isFinal)
            {
                if (_targetLength > 0)
                {
                    EncodeWindow();
                    outputWritten += DrainPending(output.Slice(outputWritten));
                    if (_pendingReadOffset < _pending.Length)
                        return OperationStatus.DestinationTooSmall;
                }

                _finished = true;
                return OperationStatus.Done;
            }

            return OperationStatus.NeedMoreData;
        }

        private void EncodeWindow()
        {
            using var target = new ByteBuffer(new Memory<byte>(_targetWindow, 0, _targetLength));
            _pending.Position = _pending.Length;
            _chunker.EncodeChunk(target, _pending);
            _targetLength = 0;
        }

        private int DrainPending(Span<byte> output)
        {
            long available = _pending.Length - _pendingReadOffset;
            if (available <= 0)
                return 0;

            int toCopy = (int)Math.Min(available, output.Length);
            if (toCopy > 0)
            {
                var sequence = _pending.GetReadOnlySequence().Slice(_pendingReadOffset, toCopy);
                int offset = 0;
                foreach (var segment in sequence)
                {
                    segment.Span.CopyTo(output.Slice(offset));
                    offset += segment.Span.Length;
                }
            }

            _pendingReadOffset += toCopy;
            if (_pendingReadOffset == _pending.Length)
            {
                _pending.SetLength(0);
                _pending.Position = 0;
                _pendingReadOffset = 0;
            }

            return toCopy;
        }

        /// <inheritdoc />
        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            _chunker.Dispose();
            _dictionary.Dispose();
            if (_ownsHasher)
                _hasher.Dispose();
            _pending.Dispose();
            _pool.Return(_targetWindow, false);
        }
    }
}
