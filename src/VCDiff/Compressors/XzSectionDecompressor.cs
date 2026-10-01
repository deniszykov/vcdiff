// Copyright (c) Metric and the Snowflake Authors.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Buffers;
using VCDiff.Compression;
using VCDiff.Compression.Xz;
using VCDiff.Shared;

namespace VCDiff.Compressors;

/// <summary>
///     Decompresses xdelta3 secondary-compressed (LZMA, id 2) window sections. Each section type (add/run data,
///     instructions and sizes, copy addresses) is one continuous XZ stream across the whole delta file, so a single
///     instance must be used for the entire file and sections must be fed in file order.
/// </summary>
internal sealed class XzSectionDecompressor : IDisposable
{
	private readonly ReadOnlySequenceStream addressesCompressedBuffer;
	private readonly XzStream addressesDecompressor;
	private readonly ReadOnlySequenceStream addRunCompressedBuffer;
	private readonly XzStream addRunDecompressor;
	private readonly ArrayPool<byte> _bytePool;
	private readonly ReadOnlySequenceStream instructionsCompressedBuffer;
	private readonly XzStream instructionsDecompressor;

	public XzSectionDecompressor(ArrayPool<byte>? bytePool = null)
	{
		this._bytePool = bytePool ?? ArrayPool<byte>.Shared;

		// Compressed sections are read in place through a resettable stream view: no copy into a memory stream.
		this.addRunCompressedBuffer = new ReadOnlySequenceStream();
		this.instructionsCompressedBuffer = new ReadOnlySequenceStream();
		this.addressesCompressedBuffer = new ReadOnlySequenceStream();

		this.addRunDecompressor = new XzStream(this.addRunCompressedBuffer, this._bytePool);
		this.instructionsDecompressor = new XzStream(this.instructionsCompressedBuffer, this._bytePool);
		this.addressesDecompressor = new XzStream(this.addressesCompressedBuffer, this._bytePool);
	}

	/// <summary>
	///     Decompresses a section supplied as a (possibly multi-segment) sequence. The sequence memory must stay valid
	///     until this call returns; it is read in place. The caller owns the returned array.
	/// </summary>
	/// <param name="windowSectionType">Which section <paramref name="sectionData" /> is.</param>
	/// <param name="sectionData">The section: its uncompressed length (a varint) followed by the XZ data.</param>
	/// <param name="maxLength">The largest uncompressed length accepted.</param>
	/// <exception cref="VcdiffException">The uncompressed length is invalid or above <paramref name="maxLength" />.</exception>
	public PooledArray Decompress(WindowSectionType windowSectionType, ReadOnlySequence<byte> sectionData, int maxLength = int.MaxValue)
	{
		ReadOnlySequenceStream compressedStream;
		XzStream xzStream;
		// ReSharper disable once SwitchStatementHandlesSomeKnownEnumValuesWithDefault
		switch (windowSectionType)
		{
			case WindowSectionType.AddRunData:
				compressedStream = this.addRunCompressedBuffer;
				xzStream = this.addRunDecompressor;
				break;
			case WindowSectionType.InstructionsAndSizes:
				compressedStream = this.instructionsCompressedBuffer;
				xzStream = this.instructionsDecompressor;
				break;
			case WindowSectionType.AddressForCopy:
				compressedStream = this.addressesCompressedBuffer;
				xzStream = this.addressesDecompressor;
				break;
			default:
				throw new ArgumentOutOfRangeException(nameof(windowSectionType));
		}

		var uncompressedLength = VarIntBe.ParseInt32(sectionData, out var uncompressedLengthByteCount);
		if (uncompressedLength < 0 || uncompressedLength > maxLength) throw VcdiffException.InvalidSecondaryCompressedSectionLength();

		var decompressedData = new PooledArray(uncompressedLength, this._bytePool);
		try
		{
			// Each section in a window uses the same compression stream throughout the file
			// If this is not the first window, reuse the same stream from before, just using different data
			compressedStream.Reset(sectionData.Slice(uncompressedLengthByteCount));
			xzStream.ReadExactOrThrow(decompressedData.AsSpan());
		}
		catch
		{
			decompressedData.Dispose();
			throw;
		}
		finally
		{
			// Do not keep a reference to caller memory past this call.
			compressedStream.Reset(ReadOnlySequence<byte>.Empty);
		}

		return decompressedData;
	}

	public void Dispose()
	{
		this.addRunDecompressor.Dispose();
		this.instructionsDecompressor.Dispose();
		this.addressesDecompressor.Dispose();
		this.addRunCompressedBuffer.Dispose();
		this.instructionsCompressedBuffer.Dispose();
		this.addressesCompressedBuffer.Dispose();
	}
}
