// Portions copyright (c) 2014 Adam Hathcock and the SharpCompress contributors.
// Licensed under the MIT License.

using System;
using System.Buffers;
using System.IO;

namespace VCDiff.Compression.Xz.Filters;

internal abstract class BlockFilter : ReadOnlyStream
{
	private const ulong LZMA2_FILTER_ID = 0x21;

	public abstract bool AllowAsLast { get; }
	public abstract bool AllowAsNonLast { get; }
	public abstract bool ChangesDataSize { get; }

	protected ArrayPool<byte>? BytePool { get; private set; }

	protected abstract void Init(ReadOnlySpan<byte> properties);

	public static BlockFilter Read(ref XzSpanReader reader, ArrayPool<byte>? bytePool = null)
	{
		var filterType = reader.ReadXzInteger();
		BlockFilter filter = filterType switch {
			LZMA2_FILTER_ID => new Lzma2Filter(),
			_ => throw VcdiffException.FilterNotImplemented(filterType)
		};
		filter.BytePool = bytePool;

		var sizeOfProperties = reader.ReadXzInteger();
		if (sizeOfProperties > int.MaxValue) throw VcdiffException.BlockFilterInfoTooLarge();

		filter.Init(reader.ReadBytes((int)sizeOfProperties));
		return filter;
	}

	public abstract void SetBaseStream(Stream stream);
}
