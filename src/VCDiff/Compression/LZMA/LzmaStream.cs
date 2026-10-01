// Portions copyright (c) 2014 Adam Hathcock and the SharpCompress contributors.
// Licensed under the MIT License.

using System;
using System.Buffers;
using System.IO;
using VCDiff.Compression.LZMA.LZ;
using VCDiff.Compression.LZMA.RangeCoder;

namespace VCDiff.Compression.LZMA;

/// <summary>
///     Minimal decode-only LZMA2 stream, adapted from SharpCompress for use by the vendored XZ decoder. Encoding and
///     raw LZMA1 support have been removed. The input stream is never owned.
/// </summary>
internal sealed class LzmaStream : Stream
{
	private readonly int _dictionarySize;
	private readonly Stream _inputStream;
	private readonly OutWindow _outWindow;
	private readonly RangeDecoder _rangeDecoder;
	private long _availableBytes;
	private LzmaDecoder? _decoder;
	private bool _endReached;
	private bool _isDisposed;
	private bool _needDictReset = true;
	private bool _needProps = true;
	private long _position;
	private byte _properties;
	private long _rangeDecoderLimit;
	private bool _uncompressedChunk;

	public override bool CanRead => true;

	public override bool CanSeek => false;

	public override bool CanWrite => false;

	public override long Length => this._position + this._availableBytes;

	public override long Position { get => this._position; set => throw new NotSupportedException(); }

	/// <param name="dictionarySizeProperty">Encoded LZMA2 dictionary size (the LZMA2 filter properties byte).</param>
	/// <param name="inputStream">Compressed input; not disposed by this stream.</param>
	/// <param name="bytePool">Pool for the dictionary window and the range-decoder input buffer.</param>
	public LzmaStream(byte dictionarySizeProperty, Stream inputStream, ArrayPool<byte>? bytePool = null)
	{
		this._outWindow = new OutWindow(bytePool);
		this._rangeDecoder = new RangeDecoder(bytePool);
		this._inputStream = inputStream;

		this._dictionarySize = 2 | (dictionarySizeProperty & 1);
		this._dictionarySize <<= (dictionarySizeProperty >> 1) + 11;

		this._outWindow.Create(this._dictionarySize);
	}

	public override void Flush()
	{
	}

	protected override void Dispose(bool disposing)
	{
		if (this._isDisposed) return;

		this._isDisposed = true;
		if (disposing)
		{
			this._outWindow.Dispose();
			this._rangeDecoder.ReleaseStream(); // returns the pooled input buffer if a chunk was abandoned mid-way
		}

		base.Dispose(disposing);
	}

	public override int Read(byte[] buffer, int offset, int count)
	{
		return this.Read(buffer.AsSpan(offset, count));
	}

	public override int Read(Span<byte> buffer)
	{
		if (this._endReached) return 0;

		var count = buffer.Length;
		var total = 0;
		while (total < count)
		{
			if (this._availableBytes == 0)
			{
				this.DecodeChunkHeader();
				if (this._endReached) break;
			}

			var toProcess = count - total;
			if (toProcess > this._availableBytes) toProcess = (int)this._availableBytes;

			this.Decode(toProcess);

			var read = this._outWindow.Read(buffer.Slice(total, toProcess));
			total += read;
			this._position += read;
			this._availableBytes -= read;

			if (this._availableBytes == 0) this.FinishChunk(toProcess);
		}

		return total;
	}

	public override int ReadByte()
	{
		if (this._endReached) return -1;

		if (this._availableBytes == 0)
		{
			this.DecodeChunkHeader();
			if (this._endReached) return -1;
		}

		this.Decode(1);

		var value = this._outWindow.ReadByte();
		this._position++;
		this._availableBytes--;

		if (this._availableBytes == 0) this.FinishChunk(1);

		return value;
	}

	/// <summary>Decodes up to <paramref name="count" /> bytes of the current chunk into the window.</summary>
	private void Decode(int count)
	{
		this._outWindow.SetLimit(count);
		if (this._uncompressedChunk)
			this._outWindow.CopyStream(this._inputStream, count);
		else if (this._decoder!.Code(this._dictionarySize, this._outWindow, this._rangeDecoder))
		{
			// End-of-stream marker: not allowed inside LZMA2.
			throw VcdiffException.LzmaDataError();
		}
	}

	/// <summary>
	///     Validates that a fully drained compressed chunk consumed exactly its declared compressed size.
	/// </summary>
	/// <param name="lastDecodeCount">The size of the last <see cref="Decode" /> request.</param>
	private void FinishChunk(int lastDecodeCount)
	{
		if (this._uncompressedChunk) return;

		if (this._decoder!.HasEndMarker) throw VcdiffException.LzmaDataError();

		// Check range corruption scenario
		if (!this._rangeDecoder.IsFinished || this._rangeDecoder.Total != this._rangeDecoderLimit)
		{
			// Stream might have End Of Stream marker
			this._outWindow.SetLimit(lastDecodeCount + 1);
			if (!this._decoder.Code(this._dictionarySize, this._outWindow, this._rangeDecoder))
			{
				this._rangeDecoder.ReleaseStream();
				throw VcdiffException.LzmaDataError();
			}
		}

		this._rangeDecoder.ReleaseStream();

		if (this._outWindow.HasPending) throw VcdiffException.LzmaDataError();
	}

	private void DecodeChunkHeader()
	{
		var control = this._inputStream.ReadByteOrThrow();

		if (control == 0x00)
		{
			if (this._decoder is { HasEndMarker: true }) throw VcdiffException.LzmaDataError();

			this._endReached = true;
			return;
		}

		if (control >= 0xE0 || control == 0x01)
		{
			this._needProps = true;
			this._needDictReset = false;
			this._outWindow.Reset();
		}
		else if (this._needDictReset) throw VcdiffException.LzmaDataError();

		if (control >= 0x80)
		{
			this._uncompressedChunk = false;

			this._availableBytes = (control & 0x1F) << 16;
			this._availableBytes += this.ReadUInt16BigEndian() + 1;

			this._rangeDecoderLimit = this.ReadUInt16BigEndian() + 1;

			if (control >= 0xC0)
			{
				this._needProps = false;
				this._properties = this._inputStream.ReadByteOrThrow();

				// State reset: SetDecoderProperties fully re-initialises every model, so the decoder (and its
				// probability arrays) is reused instead of allocated per LZMA2 chunk.
				this._decoder ??= new LzmaDecoder();
				this._decoder.SetDecoderProperties(this._properties);
			}
			else if (this._needProps)
				throw VcdiffException.LzmaDataError();
			else if (control >= 0xA0)
			{
				this._decoder ??= new LzmaDecoder();
				this._decoder.SetDecoderProperties(this._properties);
			}

			this._rangeDecoder.Init(this._inputStream, this._rangeDecoderLimit);
		}
		else if (control > 0x02)
			throw VcdiffException.LzmaDataError();
		else
		{
			this._uncompressedChunk = true;
			this._availableBytes = this.ReadUInt16BigEndian() + 1;
		}
	}

	private int ReadUInt16BigEndian()
	{
		var high = this._inputStream.ReadByteOrThrow();
		return (high << 8) | this._inputStream.ReadByteOrThrow();
	}

	public override long Seek(long offset, SeekOrigin origin)
	{
		throw new NotSupportedException();
	}

	public override void SetLength(long value)
	{
		throw new NotSupportedException();
	}

	public override void Write(byte[] buffer, int offset, int count)
	{
		throw new NotSupportedException();
	}
}
