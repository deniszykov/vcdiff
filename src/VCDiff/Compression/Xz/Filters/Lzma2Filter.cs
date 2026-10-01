// Portions copyright (c) 2014 Adam Hathcock and the SharpCompress contributors.
// Licensed under the MIT License.

using System;
using System.IO;
using VCDiff.Compression.LZMA;

namespace VCDiff.Compression.Xz.Filters;

internal sealed class Lzma2Filter : BlockFilter
{
	// Encoded dictionary size (0..40); 40 means UInt32.MaxValue.
	private const int MAX_DICTIONARY_SIZE_PROPERTY = 40;

	private byte _dictionarySizeProperty;
	public override bool AllowAsLast => true;
	public override bool AllowAsNonLast => false;
	public override bool ChangesDataSize => true;

	protected override void Init(ReadOnlySpan<byte> properties)
	{
		if (properties.Length != 1) throw new InvalidFormatException("LZMA properties unexpected length");

		var reserved = properties[0] & 0xC0;
		if (reserved != 0) throw new InvalidFormatException("Reserved bits used in LZMA properties");

		this._dictionarySizeProperty = (byte)(properties[0] & 0x3F);
		if (this._dictionarySizeProperty > MAX_DICTIONARY_SIZE_PROPERTY) throw new InvalidFormatException("Dictionary size greater than UInt32.Max");
	}

	public override void SetBaseStream(Stream stream)
	{
		// The LZMA stream is owned by this filter (disposed with it); the block input stream is not.
		this.BaseStream?.Dispose();
		this.BaseStream = new LzmaStream(this._dictionarySizeProperty, stream, this.BytePool);
	}

	public override int Read(Span<byte> buffer)
	{
		return this.BaseStream.Read(buffer);
	}

	public override int ReadByte()
	{
		return this.BaseStream.ReadByte();
	}

	protected override void Dispose(bool disposing)
	{
		// Returns the pooled LZMA window and range-decoder input buffer.
		if (disposing) this.BaseStream?.Dispose();

		base.Dispose(disposing);
	}
}
