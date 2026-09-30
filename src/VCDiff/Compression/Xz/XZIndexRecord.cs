// Portions copyright (c) 2014 Adam Hathcock and the SharpCompress contributors.
// Licensed under the MIT License.

using System.IO;

namespace VCDiff.Compression.Xz;

public class XzIndexRecord
{
	public ulong UnpaddedSize { get; private set; }
	public ulong UncompressedSize { get; private set; }

	protected XzIndexRecord()
	{
	}

	public static XzIndexRecord FromBinaryReader(BinaryReader br)
	{
		var record = new XzIndexRecord();
		record.UnpaddedSize = br.ReadXzInteger();
		record.UncompressedSize = br.ReadXzInteger();
		return record;
	}
}