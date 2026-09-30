// Portions copyright (c) 2014 Adam Hathcock and the SharpCompress contributors.
// Licensed under the MIT License.

using System;
using System.IO;
using System.Text;

namespace VCDiff.Compression.Xz;

public class XzFooter
{
	private readonly BinaryReader _reader;
	private static ReadOnlySpan<byte> MagicBytes => "YZ"u8;
	public long StreamStartPosition { get; private set; }
	public long BackwardSize { get; private set; }
	public byte[]? StreamFlags { get; private set; }

	public XzFooter(BinaryReader reader)
	{
		this._reader = reader;
		this.StreamStartPosition = reader.BaseStream.Position;
	}

	public static XzFooter FromStream(Stream stream)
	{
		var footer = new XzFooter(new BinaryReader(stream, Encoding.UTF8, true));
		footer.Process();
		return footer;
	}

	public void Process()
	{
		var crc = this._reader.ReadLittleEndianUInt32();
		var footerBytes = this._reader.ReadBytes(6);
		var myCrc = Crc32.Compute(footerBytes);
		if (crc != myCrc) throw new InvalidFormatException("Footer corrupt");

		using (var stream = new MemoryStream(footerBytes))
		using (var reader = new BinaryReader(stream))
		{
			this.BackwardSize = (reader.ReadLittleEndianUInt32() + 1) * 4;
			this.StreamFlags = reader.ReadBytes(2);
		}

		var magBy = this._reader.ReadBytes(2);
		if (!magBy.AsSpan().SequenceEqual(MagicBytes)) throw new InvalidFormatException("Magic footer missing");
	}
}