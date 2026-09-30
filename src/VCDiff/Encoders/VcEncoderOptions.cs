using System.Buffers;

namespace VCDiff.Encoders
{
    /// <summary>
    /// Options for configuring a <see cref="VcEncoder"/>.
    /// </summary>
    public class VcEncoderOptions
    {
        /// <summary>
        /// The maximum buffer size for window chunking in megabytes (MiB).
        /// </summary>
        public int MaxBufferSize { get; set; } = 1;

        /// <summary>
        /// The block size to use. Must be a power of two. No match smaller than this block size
        /// will be identified.
        /// </summary>
        public int BlockSize { get; set; } = 16;

        /// <summary>
        /// The minimum size of a string match that is worth putting into a COPY. This must be
        /// bigger than twice the block size.
        /// </summary>
        public int ChunkSize { get; set; }

        /// <summary>
        /// A <see cref="RollingHash"/> instance that can be reused across multiple encoding
        /// instances of the same block size. If provided, the caller is responsible for
        /// disposing it.
        /// </summary>
        public RollingHash? RollingHash { get; set; }

        /// <summary>
        /// The <see cref="ArrayPool{T}"/> used to rent internal byte buffers. When
        /// <see langword="null"/>, <see cref="ArrayPool{T}.Shared"/> is used.
        /// </summary>
        public ArrayPool<byte>? BytePool { get; set; }

        internal ArrayPool<byte> BytePoolOrDefault => BytePool ?? ArrayPool<byte>.Shared;
    }
}
