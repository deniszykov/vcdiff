// Portions copyright (c) 2014 Adam Hathcock and the SharpCompress contributors.
// Licensed under the MIT License.

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace VCDiff.Compression.Xz;

public partial class XZIndexRecord
{
    public ulong UnpaddedSize { get; private set; }
    public ulong UncompressedSize { get; private set; }

    protected XZIndexRecord() { }

    public static XZIndexRecord FromBinaryReader(BinaryReader br)
    {
        var record = new XZIndexRecord();
        record.UnpaddedSize = br.ReadXZInteger();
        record.UncompressedSize = br.ReadXZInteger();
        return record;
    }
}
