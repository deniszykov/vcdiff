// Portions copyright (c) 2014 Adam Hathcock and the SharpCompress contributors.
// Licensed under the MIT License.

using System;
using System.Buffers;
using System.IO;
using System.Runtime.CompilerServices;

namespace VCDiff.Compression.LZMA.RangeCoder;

internal class Encoder
{
	public const uint K_TOP_VALUE = 1 << 24;
	private byte _cache;
	private uint _cacheSize;

	public ulong Low;
	public uint Range;

	private Stream _stream = null!;

	public void SetStream(Stream stream)
	{
		this._stream = stream;
	}

	public void ReleaseStream()
	{
		this._stream = null!;
	}

	public void Init()
	{
		this.Low = 0;
		this.Range = 0xFFFFFFFF;
		this._cacheSize = 1;
		this._cache = 0;
	}

	public void FlushData()
	{
		for (var i = 0; i < 5; i++) this.ShiftLow();
	}

	public void FlushStream()
	{
		this._stream.Flush();
	}

	public void CloseStream()
	{
		this._stream.Dispose();
	}

	public void ShiftLow()
	{
		if ((uint)this.Low < 0xFF000000 || (uint)(this.Low >> 32) == 1)
		{
			var temp = this._cache;
			do
			{
				this._stream.WriteByte((byte)(temp + (this.Low >> 32)));
				temp = 0xFF;
			} while (--this._cacheSize != 0);

			this._cache = (byte)((uint)this.Low >> 24);
		}

		this._cacheSize++;
		this.Low = (uint)this.Low << 8;
	}

	public void EncodeDirectBits(uint v, int numTotalBits)
	{
		for (var i = numTotalBits - 1; i >= 0; i--)
		{
			this.Range >>= 1;
			if (((v >> i) & 1) == 1) this.Low += this.Range;
			if (this.Range < K_TOP_VALUE)
			{
				this.Range <<= 8;
				this.ShiftLow();
			}
		}
	}

	public long GetProcessedSizeAdd()
	{
		return -1;
	}
}

internal class Decoder
{
	// Buffered input used by the unsafe fast LZMA decode path (see LzmaDecoder.Fast.cs). Avoids
	// issuing a virtual Stream.ReadByte() call per consumed byte, which otherwise dominates
	// decode time.
	private const int FAST_BUFFER_SIZE = 1 << 16;

	public const uint K_TOP_VALUE = 1 << 24;
	private readonly ArrayPool<byte> _bytePool;
	public uint Code;
	private byte[] _fastBuffer = null!;

	private bool _fastBufferSafeUnbounded;
	private bool _fastEndOfStream;

	// Upper bound (in terms of _total) that the fast buffered reader is allowed to physically
	// read up to. -1 means unbounded. For LZMA2 chunks this must bound reads to the current
	// chunk's compressed size, or the next chunk header would desynchronize.
	private long _fastLimit = -1;
	public uint Range;

	public Stream Stream = null!;
	public long Total;

	internal byte[] FastBufferArray => this._fastBuffer ??= this._bytePool.Rent(FAST_BUFFER_SIZE);

	internal int FastBufferPos { get; set; }

	internal int FastBufferLen { get; private set; }

	public bool IsFinished => this.Code == 0;

	public Decoder(ArrayPool<byte>? bytePool = null)
	{
		this._bytePool = bytePool ?? ArrayPool<byte>.Shared;
	}

	public void SetFastLimit(long limit)
	{
		this._fastLimit = limit;
	}

	public void Init(Stream stream)
	{
		this.Stream = stream;

		this.Code = 0;
		this.Range = 0xFFFFFFFF;
		for (var i = 0; i < 5; i++) this.Code = (this.Code << 8) | (byte)this.Stream.ReadByte();

		this.Total = 5;

		this._fastLimit = -1;
		this.FastBufferPos = 0;
		this.FastBufferLen = 0;
		this._fastEndOfStream = false;
		this._fastBufferSafeUnbounded = false;
	}

	public void ReleaseStream()
	{
		this.ReleaseFastBuffer();
		this.Stream = null!;
	}

	internal void AddTotal(long consumed)
	{
		this.Total += consumed;
	}

	internal void RefillFast()
	{
		this.FillFastBuffer();
	}

	private void FillFastBuffer()
	{
		this._fastBuffer ??= this._bytePool.Rent(FAST_BUFFER_SIZE);
		if (this._fastEndOfStream)
		{
			this.FastBufferPos = 0;
			this.FastBufferLen = 1;
			this._fastBuffer[0] = 0xFF;
			return;
		}

		var requestSize = this._fastBuffer.Length;
		if (this._fastLimit >= 0)
		{
			var remaining = this._fastLimit - this.Total;
			requestSize = remaining <= 0 ? 1 : (int)Math.Min(requestSize, remaining);
		}
		else if (!this._fastBufferSafeUnbounded) requestSize = 1;

		var read = this.Stream.Read(this._fastBuffer, 0, requestSize);
		if (read <= 0)
		{
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

	public void Normalize()
	{
		while (this.Range < K_TOP_VALUE)
		{
			this.Code = (this.Code << 8) | (byte)this.Stream.ReadByte();
			this.Range <<= 8;
			this.Total++;
		}
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public void Normalize2()
	{
		if (this.Range < K_TOP_VALUE)
		{
			this.Code = (this.Code << 8) | (byte)this.Stream.ReadByte();
			this.Range <<= 8;
			this.Total++;
		}
	}

	public uint GetThreshold(uint total)
	{
		return this.Code / (this.Range /= total);
	}

	public void Decode(uint start, uint size)
	{
		this.Code -= start * this.Range;
		this.Range *= size;
		this.Normalize();
	}

	public uint DecodeDirectBits(int numTotalBits)
	{
		var range = this.Range;
		var code = this.Code;
		uint result = 0;
		for (var i = numTotalBits; i > 0; i--)
		{
			range >>= 1;
			var t = (code - range) >> 31;
			code -= range & (t - 1);
			result = (result << 1) | (1 - t);

			if (range < K_TOP_VALUE)
			{
				code = (code << 8) | (byte)this.Stream.ReadByte();
				range <<= 8;
				this.Total++;
			}
		}

		this.Range = range;
		this.Code = code;
		return result;
	}

	public uint DecodeBit(uint size0, int numTotalBits)
	{
		var newBound = (this.Range >> numTotalBits) * size0;
		uint symbol;
		if (this.Code < newBound)
		{
			symbol = 0;
			this.Range = newBound;
		}
		else
		{
			symbol = 1;
			this.Code -= newBound;
			this.Range -= newBound;
		}

		this.Normalize();
		return symbol;
	}
}