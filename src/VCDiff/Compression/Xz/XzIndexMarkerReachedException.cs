// Portions copyright (c) 2014 Adam Hathcock and the SharpCompress contributors.
// Licensed under the MIT License.

namespace VCDiff.Compression.Xz;

/// <summary>
///     Control-flow signal: a zero block-header size byte, i.e. the index indicator, was read where the next block
///     was expected.
/// </summary>
internal class XzIndexMarkerReachedException : CompressionException
{
}
