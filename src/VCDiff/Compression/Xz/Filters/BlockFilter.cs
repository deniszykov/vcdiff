// Portions copyright (c) 2014 Adam Hathcock and the SharpCompress contributors.
// Licensed under the MIT License.

using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;

namespace VCDiff.Compression.Xz.Filters;

public abstract class BlockFilter : ReadOnlyStream
{
	private enum FilterTypes : ulong
	{
		Lzma2 = 0x21
	}

	private static readonly Dictionary<FilterTypes, Func<BlockFilter>> FilterMap = new() {
		{ FilterTypes.Lzma2, () => new Lzma2Filter() }
	};

	public abstract bool AllowAsLast { get; }
	public abstract bool AllowAsNonLast { get; }
	public abstract bool ChangesDataSize { get; }

	internal ArrayPool<byte>? BytePool { get; set; }

	public abstract void Init(byte[] properties);
	public abstract void ValidateFilter();

	public static BlockFilter Read(BinaryReader reader, ArrayPool<byte>? bytePool = null)
	{
		var filterType = (FilterTypes)reader.ReadXzInteger();
		if (!FilterMap.TryGetValue(filterType, out var createFilter)) throw new NotImplementedException($"Filter {filterType} has not yet been implemented");

		var filter = createFilter();
		filter.BytePool = bytePool;

		var sizeOfProperties = reader.ReadXzInteger();
		if (sizeOfProperties > int.MaxValue) throw new InvalidFormatException("Block filter information too large");

		var properties = reader.ReadBytes((int)sizeOfProperties);
		filter.Init(properties);
		return filter;
	}

	public abstract void SetBaseStream(Stream stream);
}