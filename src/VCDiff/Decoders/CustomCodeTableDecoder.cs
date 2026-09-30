// Copyright (c) Metric and the Snowflake Authors.
// Licensed under the Apache License, Version 2.0.

using System.IO;
using VCDiff.Includes;
using VCDiff.Shared;

namespace VCDiff.Decoders;

internal class CustomCodeTableDecoder
{
	public byte NearSize { get; private set; }

	public byte SameSize { get; private set; }

	public CodeTable? CustomTable { get; private set; }

	internal VcDiffResult Decode(IByteBuffer source)
	{
		//the custom codetable itself is a VCDiff file but it is required to be encoded with the standard table
		//the length should be the first thing after the hdr_indicator if not supporting compression
		//at least according to the RFC specs.
		var lengthOfCodeTable = VarIntBe.ParseInt32(source);

		if (lengthOfCodeTable == 0) return VcDiffResult.ERROR;

		using var codeTable = new ByteBuffer(source.ReadBytes(lengthOfCodeTable).ToArray());

		//according to the RFC specifications the next two items will be the size of near and size of same
		//they are bytes in the RFC spec, but for some reason Google uses the varint to read which does
		//the same thing if it is a single byte
		//but I am going to just read in bytes because it is the RFC standard
		this.NearSize = codeTable.ReadByte();
		this.SameSize = codeTable.ReadByte();

		if (this.NearSize == 0 || this.SameSize == 0 || this.NearSize > byte.MaxValue || this.SameSize > byte.MaxValue) return VcDiffResult.ERROR;

		this.CustomTable = new CodeTable();

		//get the original bytes of the default codetable to use as a dictionary
		using var dictionary = this.CustomTable.GetBytes();

		//Decode the code table VCDiff file itself
		//stream the decoded output into a memory stream
		using var sout = new MemoryStream();
		var decoder = new VcDecoderEx<ByteBuffer, ByteBuffer>(dictionary, codeTable, sout);
		var result = decoder.Decode(out var bytesWritten);

		if (result != VcDiffResult.SUCCESS || bytesWritten == 0) return VcDiffResult.ERROR;

		//set the new table data that was decoded
		if (!this.CustomTable.SetBytes(sout.ToArray())) result = VcDiffResult.ERROR;

		return result;
	}
}