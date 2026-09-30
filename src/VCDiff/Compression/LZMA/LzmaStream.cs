// Portions copyright (c) 2014 Adam Hathcock and the SharpCompress contributors.
// Licensed under the MIT License.

using System;
using System.Buffers;
using System.Buffers.Binary;
using System.IO;
using VCDiff.Compression.LZMA.LZ;

namespace VCDiff.Compression.LZMA;

/// <summary>
///     Minimal decode-only LZMA / LZMA2 stream, adapted from SharpCompress for use by the
///     vendored XZ decoder. Encoding support has been removed.
/// </summary>
internal class LzmaStream : Stream
{
	private readonly int _dictionarySize;
	private readonly long _inputSize;
	private readonly Stream? _inputStream;

	// LZMA2
	private readonly bool _isLzma2;
	private readonly bool _leaveOpen;
	private readonly long _outputSize;
	private readonly OutWindow _outWindow;
	private readonly RangeCoder.Decoder _rangeDecoder;
	private long _availableBytes;
	private Decoder? _decoder;
	private bool _endReached;
	private long _inputPosition;

	private bool _isDisposed;
	private bool _needDictReset = true;
	private bool _needProps = true;

	private long _position;
	private long _rangeDecoderLimit;
	private bool _uncompressedChunk;

	public override bool CanRead => true;

	public override bool CanSeek => false;

	public override bool CanWrite => false;

	public override long Length => this._position + this._availableBytes;

	public override long Position { get => this._position; set => throw new NotSupportedException(); }

	public byte[] Properties { get; } = new byte[5];

	private LzmaStream
	(
		byte[] properties,
		Stream inputStream,
		long inputSize,
		long outputSize,
		bool isLzma2,
		bool leaveOpen = false,
		ArrayPool<byte>? bytePool = null
	)
	{
		this._outWindow = new OutWindow(bytePool);
		this._rangeDecoder = new RangeCoder.Decoder(bytePool);
		this._inputStream = inputStream;
		this._inputSize = inputSize;
		this._outputSize = outputSize;
		this._isLzma2 = isLzma2;
		this._leaveOpen = leaveOpen;
		if (!isLzma2)
		{
			this._dictionarySize = BinaryPrimitives.ReadInt32LittleEndian(properties.AsSpan(1));
			this._outWindow.Create(this._dictionarySize);

			this._decoder = new Decoder();
			this._decoder.SetDecoderProperties(properties);
			this.Properties = properties;

			this._availableBytes = outputSize < 0 ? long.MaxValue : outputSize;
			this._rangeDecoderLimit = inputSize;
		}
		else
		{
			this._dictionarySize = 2 | (properties[0] & 1);
			this._dictionarySize <<= (properties[0] >> 1) + 11;

			this._outWindow.Create(this._dictionarySize);

			this.Properties = new byte[1];
			this._availableBytes = 0;
		}
	}

	public static LzmaStream Create
	(
		byte[] properties,
		Stream inputStream,
		ArrayPool<byte>? bytePool = null,
		bool leaveOpen = false
	)
	{
		return Create(properties, inputStream, -1, -1, null, properties.Length < 5, leaveOpen, bytePool);
	}

	public static LzmaStream Create
	(
		byte[] properties,
		Stream inputStream,
		long inputSize,
		bool leaveOpen = false,
		ArrayPool<byte>? bytePool = null
	)
	{
		return Create(properties, inputStream, inputSize, -1, null, properties.Length < 5, leaveOpen, bytePool);
	}

	public static LzmaStream Create
	(
		byte[] properties,
		Stream inputStream,
		long inputSize,
		long outputSize,
		bool leaveOpen = false,
		ArrayPool<byte>? bytePool = null
	)
	{
		return Create(
			properties,
			inputStream,
			inputSize,
			outputSize,
			null,
			properties.Length < 5,
			leaveOpen,
			bytePool
		);
	}

	private static LzmaStream Create
	(
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
			if (presetDictionary != null) lzma._outWindow.Train(presetDictionary);

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

	public override void Flush()
	{
	}

	protected override void Dispose(bool disposing)
	{
		if (this._isDisposed) return;

		this._isDisposed = true;
		if (disposing)
		{
			if (!this._leaveOpen) this._inputStream?.Dispose();

			this._outWindow.Dispose();
		}

		base.Dispose(disposing);
	}

	public override int Read(byte[] buffer, int offset, int count)
	{
		if (this._endReached) return 0;

		var total = 0;
		while (total < count)
		{
			if (this._availableBytes == 0)
			{
				if (this._isLzma2)
					this.DecodeChunkHeader();
				else
					this._endReached = true;
				if (this._endReached) break;
			}

			var toProcess = count - total;
			if (toProcess > this._availableBytes) toProcess = (int)this._availableBytes;

			this._outWindow.SetLimit(toProcess);
			if (this._uncompressedChunk)
				this._inputPosition += this._outWindow.CopyStream(this._inputStream!, toProcess);
			else if (this._decoder!.Code(this._dictionarySize, this._outWindow, this._rangeDecoder)) this.HandleEndMarker();

			var read = this._outWindow.Read(buffer, offset, toProcess);
			total += read;
			offset += read;
			this._position += read;
			this._availableBytes -= read;

			if (this._availableBytes == 0 && !this._uncompressedChunk)
			{
				if (this._isLzma2 && this._decoder!.HasEndMarker) throw new DataErrorException();

				// Check range corruption scenario
				if (
					!this._rangeDecoder.IsFinished || (this._rangeDecoderLimit >= 0 && this._rangeDecoder.Total != this._rangeDecoderLimit)
				)
				{
					// Stream might have End Of Stream marker
					this._outWindow.SetLimit(toProcess + 1);
					if (!this._decoder!.Code(this._dictionarySize, this._outWindow, this._rangeDecoder))
					{
						this._rangeDecoder.ReleaseStream();
						throw new DataErrorException();
					}
				}

				this._rangeDecoder.ReleaseStream();

				this._inputPosition += this._rangeDecoder.Total;
				if (this._outWindow.HasPending) throw new DataErrorException();
			}
		}

		if (this._endReached)
		{
			if (this._inputSize >= 0 && this._inputPosition != this._inputSize) throw new DataErrorException();

			if (this._outputSize >= 0 && this._position != this._outputSize) throw new DataErrorException();
		}

		return total;
	}

	public override int ReadByte()
	{
		if (this._endReached) return -1;

		if (this._availableBytes == 0)
		{
			if (this._isLzma2)
				this.DecodeChunkHeader();
			else
				this._endReached = true;
		}

		if (this._endReached)
		{
			if (this._inputSize >= 0 && this._inputPosition != this._inputSize) throw new DataErrorException();

			if (this._outputSize >= 0 && this._position != this._outputSize) throw new DataErrorException();

			return -1;
		}

		this._outWindow.SetLimit(1);
		if (this._uncompressedChunk)
			this._inputPosition += this._outWindow.CopyStream(this._inputStream!, 1);
		else if (this._decoder!.Code(this._dictionarySize, this._outWindow, this._rangeDecoder)) this.HandleEndMarker();

		var value = this._outWindow.ReadByte();
		this._position++;
		this._availableBytes--;

		if (this._availableBytes == 0 && !this._uncompressedChunk)
		{
			if (this._isLzma2 && this._decoder!.HasEndMarker) throw new DataErrorException();

			// Check range corruption scenario
			if (
				!this._rangeDecoder.IsFinished || (this._rangeDecoderLimit >= 0 && this._rangeDecoder.Total != this._rangeDecoderLimit)
			)
			{
				// Stream might have End Of Stream marker
				this._outWindow.SetLimit(2);
				if (!this._decoder!.Code(this._dictionarySize, this._outWindow, this._rangeDecoder))
				{
					this._rangeDecoder.ReleaseStream();
					throw new DataErrorException();
				}
			}

			this._rangeDecoder.ReleaseStream();

			this._inputPosition += this._rangeDecoder.Total;
			if (this._outWindow.HasPending) throw new DataErrorException();
		}

		return value;
	}

	private void DecodeChunkHeader()
	{
		var control = this._inputStream!.ReadByte();
		this._inputPosition++;

		if (control == 0x00)
		{
			if (this._isLzma2 && this._decoder is { HasEndMarker: true }) throw new DataErrorException();

			this._endReached = true;
			return;
		}

		if (control >= 0xE0 || control == 0x01)
		{
			this._needProps = true;
			this._needDictReset = false;
			this._outWindow.Reset();
		}
		else if (this._needDictReset) throw new DataErrorException();

		if (control >= 0x80)
		{
			this._uncompressedChunk = false;

			this._availableBytes = (control & 0x1F) << 16;
			this._availableBytes += (this._inputStream.ReadByte() << 8) + this._inputStream.ReadByte() + 1;
			this._inputPosition += 2;

			this._rangeDecoderLimit = (this._inputStream.ReadByte() << 8) + this._inputStream.ReadByte() + 1;
			this._inputPosition += 2;

			if (control >= 0xC0)
			{
				this._needProps = false;
				this.Properties[0] = (byte)this._inputStream.ReadByte();
				this._inputPosition++;

				this._decoder = new Decoder();
				this._decoder.SetDecoderProperties(this.Properties);
			}
			else if (this._needProps)
				throw new DataErrorException();
			else if (control >= 0xA0)
			{
				this._decoder = new Decoder();
				this._decoder.SetDecoderProperties(this.Properties);
			}

			this._rangeDecoder.Init(this._inputStream);
			this._rangeDecoder.SetFastLimit(this._rangeDecoderLimit);
		}
		else if (control > 0x02)
			throw new DataErrorException();
		else
		{
			this._uncompressedChunk = true;
			this._availableBytes = (this._inputStream.ReadByte() << 8) + this._inputStream.ReadByte() + 1;
			this._inputPosition += 2;
		}
	}

	private void HandleEndMarker()
	{
		if (this._isLzma2) throw new DataErrorException();

		if (this._outputSize < 0) this._availableBytes = this._outWindow.AvailableBytes;
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