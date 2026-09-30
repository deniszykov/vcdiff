// Copyright (c) Metric and the Snowflake Authors.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Buffers;
using System.IO;
using VCDiff.Compression;
using VCDiff.Compression.Xz;
using VCDiff.Shared;

namespace VCDiff.Compressors;

internal class XzCompressor : ICompressor, IDisposable
{
	private readonly ArrayPool<byte> _bytePool;
	private readonly MemoryStream addressesCompressedBuffer;
	private readonly XzStream addressesDecompressor;

	private readonly MemoryStream addRunCompressedBuffer;
	private readonly XzStream addRunDecompressor;
	private readonly MemoryStream instructionsCompressedBuffer;
	private readonly XzStream instructionsDecompressor;

	public XzCompressor(ArrayPool<byte>? bytePool = null)
	{
		this._bytePool = bytePool ?? ArrayPool<byte>.Shared;

		this.addRunCompressedBuffer = new MemoryStream();
		this.instructionsCompressedBuffer = new MemoryStream();
		this.addressesCompressedBuffer = new MemoryStream();

		this.addRunDecompressor = new XzStream(this.addRunCompressedBuffer, this._bytePool);
		this.instructionsDecompressor = new XzStream(this.instructionsCompressedBuffer, this._bytePool);
		this.addressesDecompressor = new XzStream(this.addressesCompressedBuffer, this._bytePool);
	}

	public PinnedArrayRental Decompress(WindowSectionType windowSectionType, PinnedArrayRental sectionData)
	{
		if (sectionData.Data == null) throw new ArgumentException("Cannot decompress null data");

		MemoryStream memoryStream;
		XzStream xzStream;
		// ReSharper disable once SwitchStatementHandlesSomeKnownEnumValuesWithDefault
		switch (windowSectionType)
		{
			case WindowSectionType.AddRunData:
				memoryStream = this.addRunCompressedBuffer;
				xzStream = this.addRunDecompressor;
				break;
			case WindowSectionType.InstructionsAndSizes:
				memoryStream = this.instructionsCompressedBuffer;
				xzStream = this.instructionsDecompressor;
				break;
			case WindowSectionType.AddressForCopy:
				memoryStream = this.addressesCompressedBuffer;
				xzStream = this.addressesDecompressor;
				break;
			default:
				throw new ArgumentOutOfRangeException(nameof(windowSectionType));
		}

		var uncompressedLength = VarIntBe.ParseInt32(sectionData.AsSpan(), out var uncompressedLengthByteCount);
		var compressedData = sectionData.AsSpan().Slice(uncompressedLengthByteCount);

		// Each section in a window uses the same compression stream throughout the file
		// If this is not the first window, reuse the same stream from before, just using different data
		memoryStream.SetLength(compressedData.Length);
		memoryStream.Position = 0;
		memoryStream.Write(compressedData);
		memoryStream.Position = 0;

		var decompressedData = new PinnedArrayRental(uncompressedLength, this._bytePool);
		xzStream.ReadExactly(decompressedData.AsSpan());

		return decompressedData;
	}
	public void Dispose()
	{
		this.addressesCompressedBuffer.Dispose();
		this.instructionsCompressedBuffer.Dispose();
		this.addressesCompressedBuffer.Dispose();
		this.addRunDecompressor.Dispose();
		this.instructionsDecompressor.Dispose();
		this.addressesDecompressor.Dispose();
	}
}