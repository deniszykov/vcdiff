// Portions copyright (c) 2014 Adam Hathcock and the SharpCompress contributors.
// Licensed under the MIT License.

using System;
using System.Buffers;
using System.IO;

namespace VCDiff.Compression.LZMA.LZ;

/// <summary>
///     LZ dictionary / output window. Decoded bytes accumulate here (pooled circular buffer) and are copied out with
///     <see cref="Read" />; the window position wraps once the caller has drained the window end.
/// </summary>
internal sealed class OutWindow : IDisposable
{
	private readonly ArrayPool<byte> _bytePool;
	private int _pendingDist;
	private int _pendingLen;
	private int _streamPos;

	// Fast-path accessors used by the local-variable LZMA decode loop (LzmaDecoder.Fast.cs).
	internal byte[] FastBuffer { get; private set; } = null!;
	internal int FastPos { get; set; }
	internal long FastTotal { get; set; }
	internal int FastWindowSize { get; private set; }
	internal long FastLimit { get; private set; }

	private bool HasSpace => this.FastPos < this.FastWindowSize && this.FastTotal < this.FastLimit;

	public bool HasPending => this._pendingLen > 0;

	public OutWindow(ArrayPool<byte>? bytePool = null)
	{
		this._bytePool = bytePool ?? ArrayPool<byte>.Shared;
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
		this.FastTotal = 0;
		this.FastLimit = 0;
	}

	/// <summary>Dictionary reset: forgets all history.</summary>
	public void Reset()
	{
		this.Create(this.FastWindowSize);
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

	private void PutByte(byte b)
	{
		this.FastBuffer[this.FastPos++] = b;
		this.FastTotal++;
	}

	/// <summary>Copies up to <paramref name="len" /> stored (uncompressed-chunk) bytes from the input.</summary>
	public int CopyStream(Stream stream, int len)
	{
		var size = len;
		while (size > 0 && this.FastPos < this.FastWindowSize && this.FastTotal < this.FastLimit)
		{
			var curSize = this.FastWindowSize - this.FastPos;
			if (curSize > this.FastLimit - this.FastTotal) curSize = (int)(this.FastLimit - this.FastTotal);
			if (curSize > size) curSize = size;
			var numReadBytes = stream.Read(this.FastBuffer, this.FastPos, curSize);
			if (numReadBytes == 0) throw new IncompleteArchiveException("Unexpected end of LZMA2 uncompressed chunk.");

			size -= numReadBytes;
			this.FastPos += numReadBytes;
			this.FastTotal += numReadBytes;
		}

		return len - size;
	}

	public void SetLimit(long size)
	{
		this.FastLimit = this.FastTotal + size;
	}

	/// <summary>
	///     Copies decoded bytes out of the window into <paramref name="buffer" /> (the one unavoidable copy:
	///     the window must keep its history for back-references).
	/// </summary>
	public int Read(Span<byte> buffer)
	{
		if (this._streamPos >= this.FastPos) return 0;

		var size = this.FastPos - this._streamPos;
		if (size > buffer.Length) size = buffer.Length;

		this.FastBuffer.AsSpan(this._streamPos, size).CopyTo(buffer);
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
		if (this.FastBuffer is null) return;

		this._bytePool.Return(this.FastBuffer);
		this.FastBuffer = null!;
	}
}
