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
