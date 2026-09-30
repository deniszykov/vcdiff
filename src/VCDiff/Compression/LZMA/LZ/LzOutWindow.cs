// Portions copyright (c) 2014 Adam Hathcock and the SharpCompress contributors.
// Licensed under the MIT License.

using System;
using System.Buffers;
using System.IO;

namespace VCDiff.Compression.LZMA.LZ;

internal class OutWindow : IDisposable
{
	private readonly ArrayPool<byte> _bytePool;
	private int _pendingDist;
	private int _pendingLen;
	private Stream? _stream;
	private int _streamPos;

	public long Total { get; private set; }

	// Fast-path accessors used by the local-variable LZMA decode loop (LzmaDecoder.Fast.cs).
	internal byte[] FastBuffer { get; private set; } = null!;
	internal int FastPos { get; set; }
	internal long FastTotal { get => this.Total; set => this.Total = value; }
	internal int FastWindowSize { get; private set; }
	internal long FastLimit { get; private set; }

	public bool HasSpace => this.FastPos < this.FastWindowSize && this.Total < this.FastLimit;

	public bool HasPending => this._pendingLen > 0;

	public int AvailableBytes => this.FastPos - this._streamPos;

	public OutWindow(ArrayPool<byte>? bytePool = null)
	{
		this._bytePool = bytePool ?? ArrayPool<byte>.Shared;
	}

	internal void FastFlush()
	{
		this.Flush();
	}

	internal void SetPendingFast(int distance, int len)
	{
		this._pendingDist = distance;
		this._pendingLen = len;
	}

	public void Create(int windowSize)
	{
		if (windowSize <= 0) throw new InvalidFormatException($"LZMA: invalid dictionary size {windowSize}");

		if (this.FastWindowSize != windowSize)
		{
			if (this.FastBuffer is not null) this._bytePool.Return(this.FastBuffer);

			this.FastBuffer = this._bytePool.Rent(windowSize);
		}

		this.FastBuffer[windowSize - 1] = 0;
		this.FastWindowSize = windowSize;
		this.FastPos = 0;
		this._streamPos = 0;
		this._pendingLen = 0;
		this.Total = 0;
		this.FastLimit = 0;
	}

	public void Reset()
	{
		this.ReleaseStream();
		this.Create(this.FastWindowSize);
	}

	public void Init(Stream stream)
	{
		this.ReleaseStream();
		this._stream = stream;
	}

	public void Train(Stream stream)
	{
		var len = stream.Length;
		var size = len < this.FastWindowSize ? (int)len : this.FastWindowSize;
		stream.Position = len - size;
		this.Total = 0;
		this.FastLimit = size;
		this.FastPos = this.FastWindowSize - size;
		this.CopyStream(stream, size);
		if (this.FastPos == this.FastWindowSize) this.FastPos = 0;

		this._streamPos = this.FastPos;
	}

	public void ReleaseStream()
	{
		this.Flush();
		this._stream = null;
	}

	private void Flush()
	{
		if (this._stream is null) return;

		var size = this.FastPos - this._streamPos;
		if (size == 0) return;

		this._stream.Write(this.FastBuffer, this._streamPos, size);
		if (this.FastPos >= this.FastWindowSize) this.FastPos = 0;

		this._streamPos = this.FastPos;
	}

	public void CopyPending()
	{
		if (this._pendingLen < 1) return;

		var rem = this._pendingLen;
		var pos = (this._pendingDist < this.FastPos ? this.FastPos : this.FastPos + this.FastWindowSize) - this._pendingDist - 1;
		while (rem > 0 && this.HasSpace)
		{
			if (pos >= this.FastWindowSize) pos = 0;

			this.PutByte(this.FastBuffer[pos++]);
			rem--;
		}

		this._pendingLen = rem;
	}

	public void CopyBlock(int distance, int len)
	{
		var rem = len;
		var pos = (distance < this.FastPos ? this.FastPos : this.FastPos + this.FastWindowSize) - distance - 1;
		var targetSize = this.HasSpace ? (int)Math.Min(rem, this.FastLimit - this.Total) : 0;
		var sizeUntilWindowEnd = Math.Min(this.FastWindowSize - this.FastPos, this.FastWindowSize - pos);
		var sizeUntilOverlap = Math.Abs(pos - this.FastPos);
		var fastSize = Math.Min(Math.Min(sizeUntilWindowEnd, sizeUntilOverlap), targetSize);
		if (fastSize >= 2)
		{
			this.FastBuffer.AsSpan(pos, fastSize).CopyTo(this.FastBuffer.AsSpan(this.FastPos, fastSize));
			this.FastPos += fastSize;
			pos += fastSize;
			this.Total += fastSize;
			if (this.FastPos >= this.FastWindowSize) this.Flush();
			rem -= fastSize;
		}

		while (rem > 0 && this.HasSpace)
		{
			if (pos >= this.FastWindowSize) pos = 0;

			this.PutByte(this.FastBuffer[pos++]);
			rem--;
		}

		this._pendingLen = rem;
		this._pendingDist = distance;
	}

	public void PutByte(byte b)
	{
		this.FastBuffer[this.FastPos++] = b;
		this.Total++;
		if (this.FastPos >= this.FastWindowSize) this.Flush();
	}

	public byte GetByte(int distance)
	{
		var pos = this.FastPos - distance - 1;
		if (pos < 0) pos += this.FastWindowSize;
		return this.FastBuffer[pos];
	}

	public int CopyStream(Stream stream, int len)
	{
		var size = len;
		while (size > 0 && this.FastPos < this.FastWindowSize && this.Total < this.FastLimit)
		{
			var curSize = this.FastWindowSize - this.FastPos;
			if (curSize > this.FastLimit - this.Total) curSize = (int)(this.FastLimit - this.Total);
			if (curSize > size) curSize = size;
			var numReadBytes = stream.Read(this.FastBuffer, this.FastPos, curSize);
			if (numReadBytes == 0) throw new DataErrorException();

			size -= numReadBytes;
			this.FastPos += numReadBytes;
			this.Total += numReadBytes;
			if (this.FastPos >= this.FastWindowSize) this.Flush();
		}

		return len - size;
	}

	public void SetLimit(long size)
	{
		this.FastLimit = this.Total + size;
	}

	public int Read(byte[] buffer, int offset, int count)
	{
		if (this._streamPos >= this.FastPos) return 0;

		var size = this.FastPos - this._streamPos;
		if (size > count) size = count;
		Buffer.BlockCopy(this.FastBuffer, this._streamPos, buffer, offset, size);
		this._streamPos += size;
		if (this._streamPos >= this.FastWindowSize)
		{
			this.FastPos = 0;
			this._streamPos = 0;
		}

		return size;
	}

	public int Read(Memory<byte> buffer, int offset, int count)
	{
		if (this._streamPos >= this.FastPos) return 0;

		var size = this.FastPos - this._streamPos;
		if (size > count) size = count;

		this.FastBuffer.AsMemory(this._streamPos, size).CopyTo(buffer.Slice(offset, size));
		this._streamPos += size;
		if (this._streamPos >= this.FastWindowSize)
		{
			this.FastPos = 0;
			this._streamPos = 0;
		}

		return size;
	}

	public int ReadByte()
	{
		if (this._streamPos >= this.FastPos) return -1;

		int value = this.FastBuffer[this._streamPos];

		this._streamPos++;
		if (this._streamPos >= this.FastWindowSize)
		{
			this.FastPos = 0;
			this._streamPos = 0;
		}

		return value;
	}

	public void Dispose()
	{
		this.ReleaseStream();
		if (this.FastBuffer is null) return;

		this._bytePool.Return(this.FastBuffer);
		this.FastBuffer = null!;
	}
}