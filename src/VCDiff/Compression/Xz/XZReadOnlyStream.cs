// Portions copyright (c) 2014 Adam Hathcock and the SharpCompress contributors.
// Licensed under the MIT License.

using System.IO;


namespace VCDiff.Compression.Xz;

public abstract class XZReadOnlyStream : ReadOnlyStream
{
    public XZReadOnlyStream(Stream stream)
    {
        BaseStream = stream;
        if (!BaseStream.CanRead)
        {
            throw new InvalidFormatException("Must be able to read from stream");
        }
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
    }
}
