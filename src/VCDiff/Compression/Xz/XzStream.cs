// Portions copyright (c) 2014 Adam Hathcock and the SharpCompress contributors.
// Licensed under the MIT License.

using System;
using System.Buffers;
using System.IO;

namespace VCDiff.Compression.Xz;

/// <summary>
///     Sequential, decode-only reader of a single XZ stream (header, LZMA2 blocks, index, footer).
/// </summary>
internal sealed class XzStream : ReadOnlyStream
{
	private readonly ArrayPool<byte>? _bytePool;
	private CheckType _checkType;
	private XzBlock? _currentBlock;
	private bool _endOfStream;
	private bool _headerIsRead;

	public XzStream(Stream baseStream, ArrayPool<byte>? bytePool = null)
		: base(baseStream)
	{
		this._bytePool = bytePool;
	}

	protected override void Dispose(bool disposing)
	{
		if (disposing)
		{
			// Returns the current block's pooled buffers; the base stream is not owned.
			this._currentBlock?.Dispose();
			this._currentBlock = null;
		}

		base.Dispose(disposing);
	}

	public override int Read(Span<byte> buffer)
	{
		var bytesRead = 0;
		if (this._endOfStream) return bytesRead;

		if (!this._headerIsRead) this.ReadHeader();

		bytesRead = this.ReadBlocks(buffer);
		if (bytesRead < buffer.Length)
		{
			this._endOfStream = true;
			var indexSize = XzIndex.Skip(this.BaseStream);
			XzFooter.Read(this.BaseStream, this._checkType, indexSize);
		}

		return bytesRead;
	}

	private void ReadHeader()
	{
		this._checkType = XzHeader.Read(this.BaseStream);
		switch (this._checkType)
		{
			case CheckType.NONE:
			case CheckType.CRC32:
			case CheckType.CRC64:
			case CheckType.SHA256:
				break;
			default:
				throw new InvalidFormatException("Check Type unknown to this version of decoder.");
		}

		this._headerIsRead = true;
	}

	private int ReadBlocks(Span<byte> buffer)
	{
		var count = buffer.Length;
		var bytesRead = 0;
		if (this._currentBlock is null) this.NextBlock();

		for (;;)
		{
			try
			{
				if (bytesRead >= count) break;

				var remaining = count - bytesRead;
				var justRead = this._currentBlock!.Read(buffer.Slice(bytesRead, remaining));
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
		// The previous block is fully consumed (padding and check verified): release its LZMA window now.
		this._currentBlock?.Dispose();
		this._currentBlock = new XzBlock(this.BaseStream, this._checkType, this._bytePool);
	}
}
