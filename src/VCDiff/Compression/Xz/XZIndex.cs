// Portions copyright (c) 2014 Adam Hathcock and the SharpCompress contributors.
// Licensed under the MIT License.

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace VCDiff.Compression.Xz;

public class XzIndex
{
	private readonly bool _indexMarkerAlreadyVerified;
	private readonly BinaryReader _reader;
	public long StreamStartPosition { get; }
	public ulong NumberOfRecords { get; private set; }
	public List<XzIndexRecord> Records { get; } = new();

	public XzIndex(BinaryReader reader, bool indexMarkerAlreadyVerified)
	{
		this._reader = reader;
		this._indexMarkerAlreadyVerified = indexMarkerAlreadyVerified;
		this.StreamStartPosition = reader.BaseStream.Position;
		if (indexMarkerAlreadyVerified) this.StreamStartPosition--;
	}

	public static XzIndex FromStream(Stream stream, bool indexMarkerAlreadyVerified)
	{
		var index = new XzIndex(
			new BinaryReader(stream, Encoding.UTF8, true),
			indexMarkerAlreadyVerified
		);
		index.Process();
		return index;
	}

	public void Process()
	{
		if (!this._indexMarkerAlreadyVerified) this.VerifyIndexMarker();

		this.NumberOfRecords = this._reader.ReadXzInteger();
		for (ulong i = 0; i < this.NumberOfRecords; i++) this.Records.Add(XzIndexRecord.FromBinaryReader(this._reader));

		this.SkipPadding();
		this.VerifyCrc32();
	}

	private void VerifyIndexMarker()
	{
		var marker = this._reader.ReadByte();
		if (marker != 0) throw new InvalidFormatException("Not an index block");
	}

	private void SkipPadding()
	{
		var bytes = (int)(this._reader.BaseStream.Position - this.StreamStartPosition) % 4;
		if (bytes > 0)
		{
			var paddingBytes = this._reader.ReadBytes(4 - bytes);
			if (paddingBytes.Any(b => b != 0)) throw new InvalidFormatException("Padding bytes were non-null");
		}
	}

	private void VerifyCrc32()
	{
		var crc = this._reader.ReadLittleEndianUInt32();

		// TODO verify this matches
	}
}