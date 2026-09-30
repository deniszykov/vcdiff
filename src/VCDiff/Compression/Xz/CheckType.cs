// Portions copyright (c) 2014 Adam Hathcock and the SharpCompress contributors.
// Licensed under the MIT License.

namespace VCDiff.Compression.Xz;

public enum CheckType : byte
{
    NONE = 0x00,
    CRC32 = 0x01,
    CRC64 = 0x04,
    SHA256 = 0x0A,
}
