// Portions copyright (c) 2014 Adam Hathcock and the SharpCompress contributors.
// Licensed under the MIT License.

using System.IO;
using VCDiff.Compression.LZMA;

namespace VCDiff.Compression.Xz.Filters;

public class Lzma2Filter : BlockFilter
{
	private byte _dictionarySize;
	public override bool AllowAsLast => true;
	public override bool AllowAsNonLast => false;
	public override bool ChangesDataSize => true;
	public uint DictionarySize
	{
		get
		{
			if (this._dictionarySize > 40) throw new InvalidFormatException("Dictionary size greater than UInt32.Max");

			if (this._dictionarySize == 40) return uint.MaxValue;

			var mantissa = 2 | (this._dictionarySize & 1);
			var exponent = this._dictionarySize / 2 + 11;
			return (uint)mantissa << exponent;
		}
	}

	public override void Init(byte[] properties)
	{
		if (properties.Length != 1) throw new InvalidFormatException("LZMA properties unexpected length");

		this._dictionarySize = (byte)(properties[0] & 0x3F);
		var reserved = properties[0] & 0xC0;
		if (reserved != 0) throw new InvalidFormatException("Reserved bits used in LZMA properties");
	}

	public override void ValidateFilter()
	{
	}

	public override void SetBaseStream(Stream stream)
	{
		this.BaseStream = LzmaStream.Create(new[] { this._dictionarySize }, stream, this.BytePool);
	}

	public override int Read(byte[] buffer, int offset, int count)
	{
		return this.BaseStream.Read(buffer, offset, count);
	}

	public override int ReadByte()
	{
		return this.BaseStream.ReadByte();
	}
}