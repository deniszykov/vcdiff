// Portions copyright (c) 2014 Adam Hathcock and the SharpCompress contributors.
// Licensed under the MIT License.

using System;
using System.Buffers;
using System.IO;

namespace VCDiff.Compression.LZMA.RangeCoder;

/// <summary>
///     Range decoder state (range, code, consumed byte count) plus the buffered compressed input consumed by the
///     fast LZMA decode loop (see <c>LzmaDecoder.Fast.cs</c>).
/// </summary>
internal sealed class RangeDecoder
{
	public const int K_NUM_BIT_MODEL_TOTAL_BITS = 11;
	public const uint K_BIT_MODEL_TOTAL = 1 << K_NUM_BIT_MODEL_TOTAL_BITS;
	public const uint K_TOP_VALUE = 1 << 24;

	// Buffered input avoids issuing a virtual Stream.ReadByte() call per consumed byte, which
	// otherwise dominates decode time.
	private const int FAST_BUFFER_SIZE = 1 << 16;
	private const int INIT_BYTES = 5;

	private readonly ArrayPool<byte> _bytePool;
	public uint Code;
	private byte[] _fastBuffer = null!;
	private bool _fastEndOfStream;

	// Upper bound (in terms of Total) that the fast buffered reader is allowed to physically
	// read up to: the current LZMA2 chunk's compressed size, or the next chunk header would
	// desynchronize.
	private long _fastLimit;
	public uint Range;

	private Stream _stream = null!;
	public long Total;

	internal byte[] FastBufferArray => this._fastBuffer ??= this._bytePool.Rent(FAST_BUFFER_SIZE);

	internal int FastBufferPos { get; set; }

	internal int FastBufferLen { get; private set; }

	public bool IsFinished => this.Code == 0;

	public RangeDecoder(ArrayPool<byte>? bytePool = null)
	{
		this._bytePool = bytePool ?? ArrayPool<byte>.Shared;
	}

	/// <summary>
	///     Starts decoding a chunk from <paramref name="stream" /> whose compressed size (including the 5 init
	///     bytes) is <paramref name="limit" />.
	/// </summary>
	public void Init(Stream stream, long limit)
	{
		this._stream = stream;

		this.Code = 0;
		this.Range = 0xFFFFFFFF;
		for (var i = 0; i < INIT_BYTES; i++) this.Code = (this.Code << 8) | stream.ReadByteOrThrow();

		this.Total = INIT_BYTES;

		this._fastLimit = limit;
		this.FastBufferPos = 0;
		this.FastBufferLen = 0;
		this._fastEndOfStream = false;
	}

	public void ReleaseStream()
	{
		this.ReleaseFastBuffer();
		this._stream = null!;
	}

	internal void AddTotal(long consumed)
	{
		this.Total += consumed;
	}

	internal void RefillFast()
	{
		this._fastBuffer ??= this._bytePool.Rent(FAST_BUFFER_SIZE);
		if (this._fastEndOfStream)
		{
			this.FastBufferPos = 0;
			this.FastBufferLen = 1;
			this._fastBuffer[0] = 0xFF;
			return;
		}

		var remaining = this._fastLimit - this.Total;
		var requestSize = remaining <= 0 ? 1 : (int)Math.Min(this._fastBuffer.Length, remaining);

		var read = this._stream.Read(this._fastBuffer, 0, requestSize);
		if (read <= 0)
		{
			// The chunk header promised more compressed bytes than the input holds.
			if (remaining > 0) throw new IncompleteArchiveException("Unexpected end of LZMA chunk.");

			// Corrupt data asked for bytes past the chunk: feed padding, the chunk-end checks reject it.
			this._fastEndOfStream = true;
			this.FastBufferPos = 0;
			this.FastBufferLen = 1;
			this._fastBuffer[0] = 0xFF;
			return;
		}

		this.FastBufferPos = 0;
		this.FastBufferLen = read;
	}

	private void ReleaseFastBuffer()
	{
		if (this._fastBuffer is not null)
		{
			this._bytePool.Return(this._fastBuffer);
			this._fastBuffer = null!;
		}

		this.FastBufferPos = 0;
		this.FastBufferLen = 0;
		this._fastEndOfStream = false;
	}
}
