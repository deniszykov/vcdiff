// Portions copyright (c) 2014 Adam Hathcock and the SharpCompress contributors.
// Licensed under the MIT License.

using System;
using System.Buffers;
using System.IO;

namespace VCDiff.Compression.Xz;

public sealed class XzStream : XzReadOnlyStream
{
	private readonly ArrayPool<byte>? _bytePool;
	private XzBlock? _currentBlock;

	private bool _endOfStream;

	public XzHeader? Header { get; private set; }
	public XzIndex? Index { get; private set; }
	public XzFooter? Footer { get; private set; }
	public bool HeaderIsRead { get; private set; }

	public XzStream(Stream baseStream, ArrayPool<byte>? bytePool = null)
		: base(baseStream)
	{
		this._bytePool = bytePool;
	}

	protected override void Dispose(bool disposing)
	{
		base.Dispose(disposing);
	}

	public static bool IsXzStream(Stream stream)
	{
		try
		{
			return null != XzHeader.FromStream(stream);
		}
		catch (Exception)
		{
			return false;
		}
	}

	private void AssertBlockCheckTypeIsSupported()
	{
		switch (this.Header!.BlockCheckType)
		{
			case CheckType.NONE:
			case CheckType.CRC32:
			case CheckType.CRC64:
			case CheckType.SHA256:
				break;
			default:
				throw new InvalidFormatException("Check Type unknown to this version of decoder.");
		}
	}

	public override int Read(byte[] buffer, int offset, int count)
	{
		var bytesRead = 0;
		if (this._endOfStream) return bytesRead;

		if (!this.HeaderIsRead) this.ReadHeader();

		bytesRead = this.ReadBlocks(buffer, offset, count);
		if (bytesRead < count)
		{
			this._endOfStream = true;
			this.ReadIndex();
			this.ReadFooter();
		}

		return bytesRead;
	}

	private void ReadHeader()
	{
		this.Header = XzHeader.FromStream(this.BaseStream);
		this.AssertBlockCheckTypeIsSupported();
		this.HeaderIsRead = true;
	}

	private void ReadIndex()
	{
		this.Index = XzIndex.FromStream(this.BaseStream, true);
	}

	// TODO verify Index
	private void ReadFooter()
	{
		this.Footer = XzFooter.FromStream(this.BaseStream);
	}

	// TODO verify footer

	private int ReadBlocks(byte[] buffer, int offset, int count)
	{
		var bytesRead = 0;
		if (this._currentBlock is null) this.NextBlock();

		for (;;)
		{
			try
			{
				if (bytesRead >= count) break;

				var remaining = count - bytesRead;
				var newOffset = offset + bytesRead;
				var justRead = this._currentBlock!.Read(buffer, newOffset, remaining);
				if (justRead < remaining) this.NextBlock();

				bytesRead += justRead;
			}
			catch (XzIndexMarkerReachedException)
			{
				break;
			}
		}

		return bytesRead;
	}

	private void NextBlock()
	{
		this._currentBlock = new XzBlock(this.BaseStream, this.Header!.BlockCheckType, this.Header!.BlockCheckSize, this._bytePool);
	}
}