using System;
using System.IO;
using Microsoft.IO;
using VCDiff.Shared;

namespace VCDiff
{
    /// <summary>
    /// Helpers for preparing VCDIFF dictionaries.
    /// </summary>
    public static class VcDiff
    {
        /// <summary>
        /// Reads an entire dictionary <paramref name="stream"/> into a pooled
        /// <see cref="RecyclableMemoryStream"/>.
        /// </summary>
        /// <param name="stream">The stream containing dictionary data.</param>
        /// <param name="tag">An optional tag used to identify the pooled stream.</param>
        /// <returns>
        /// A <see cref="RecyclableMemoryStream"/> positioned at the start. The caller owns the
        /// returned stream and is responsible for disposing it. Pass its
        /// <see cref="RecyclableMemoryStream.GetReadOnlySequence"/> to a
        /// <see cref="Encoders.VcDiffEncoder"/> or <see cref="Decoders.VcDiffDecoder"/>.
        /// </returns>
        public static RecyclableMemoryStream ReadDictionary(Stream stream, string? tag = null)
        {
            if (stream == null)
                throw new ArgumentNullException(nameof(stream));

            var ms = Pool.MemoryStreamManager.GetStream(tag ?? nameof(VcDiff));
            stream.CopyTo(ms);
            ms.Position = 0;
            return ms;
        }

    }
}
