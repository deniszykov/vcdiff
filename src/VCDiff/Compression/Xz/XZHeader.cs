// Portions copyright (c) 2014 Adam Hathcock and the SharpCompress contributors.
// Licensed under the MIT License.

using System.IO;
using System.Linq;
using System.Text;

namespace VCDiff.Compression.Xz;

public class XzHeader
{
	private readonly BinaryReader _reader;
	private readonly byte[] magicHeader = { 0xFD, 0x37, 0x7A, 0x58, 0x5a, 0x00 };

	public CheckType BlockCheckType { get; private set; }
	public int BlockCheckSize => 4 << (((int)this.BlockCheckType + 2) / 3 - 1);

	public XzHeader(BinaryReader reader)
	{
		this._reader = reader;
	}

	public static XzHeader FromStream(Stream stream)
	{
		var header = new XzHeader(new BinaryReader(stream, Encoding.UTF8, true));
		header.Process();
		return header;
	}

	public void Process()
	{
		this.CheckMagicBytes(this._reader.ReadBytes(6));
		this.ProcessStreamFlags();
	}

	private void ProcessStreamFlags()
	{
		var streamFlags = this._reader.ReadBytes(2);
		var crc = this._reader.ReadLittleEndianUInt32();
		var calcCrc = Crc32.Compute(streamFlags);
		if (crc != calcCrc) throw new InvalidFormatException("Stream header corrupt");

		this.BlockCheckType = (CheckType)(streamFlags[1] & 0x0F);
		var futureUse = (byte)(streamFlags[1] & 0xF0);
		if (futureUse != 0 || streamFlags[0] != 0) throw new InvalidFormatException("Unknown XZ Stream Version");
	}

	private void CheckMagicBytes(byte[] header)
	{
		if (!header.SequenceEqual(this.magicHeader)) throw new InvalidFormatException("Invalid XZ Stream");
	}
}