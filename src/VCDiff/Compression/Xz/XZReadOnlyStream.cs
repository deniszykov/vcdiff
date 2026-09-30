// Portions copyright (c) 2014 Adam Hathcock and the SharpCompress contributors.
// Licensed under the MIT License.

using System.IO;

namespace VCDiff.Compression.Xz;

public abstract class XzReadOnlyStream : ReadOnlyStream
{
	public XzReadOnlyStream(Stream stream)
	{
		this.BaseStream = stream;
		if (!this.BaseStream.CanRead) throw new InvalidFormatException("Must be able to read from stream");
	}

	protected override void Dispose(bool disposing)
	{
		base.Dispose(disposing);
	}
}