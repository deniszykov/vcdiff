// Copyright (c) Metric and the Snowflake Authors.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Buffers;
using VCDiff.Includes;
using VCDiff.Shared;

namespace VCDiff.Decoders;

/// <summary>
///     Decodes the custom code table section of a delta header (RFC 3284 section 7): the near and same cache sizes
///     followed by the code table, itself delta-encoded as a full VCDIFF stream against the serialized default
///     code table.
/// </summary>
internal sealed class CustomCodeTableDecoder
{
	private readonly ArrayPool<byte> _bytePool;

	public byte NearSize { get; private set; }

	public byte SameSize { get; private set; }

	public CodeTable? CustomTable { get; private set; }

	public CustomCodeTableDecoder(ArrayPool<byte> bytePool)
	{
		this._bytePool = bytePool;
	}

	/// <summary>
	///     Decodes the section from <paramref name="source" />: the code table length (a varint) followed by that
	///     many bytes. The sequence must hold the whole section.
	/// </summary>
	internal bool Decode(ReadOnlySequence<byte> source)
	{
		var result = VarIntBe.TryParseInt32(source, out var lengthOfCodeTable, out var lengthByteCount);
		if (result != ParseResult.Success || lengthOfCodeTable <= 0)
			return false;

		var codeTable = source.Slice(lengthByteCount);
		if (codeTable.Length < lengthOfCodeTable)
			return false;

		codeTable = codeTable.Slice(0, lengthOfCodeTable);
		if (codeTable.IsSingleSegment)
			return this.DecodeCore(codeTable.FirstSpan);

		// Rare multi-segment case: consolidate the (small) code table into a pooled contiguous buffer.
		var rented = this._bytePool.Rent(lengthOfCodeTable);
		try
		{
			codeTable.CopyTo(rented);
			return this.DecodeCore(rented.AsSpan(0, lengthOfCodeTable));
		}
		finally
		{
			this._bytePool.Return(rented);
		}
	}

	private bool DecodeCore(ReadOnlySpan<byte> codeTable)
	{
		// The near and same sizes are single bytes in the RFC (open-vcdiff reads them as varints, which is the
		// same for the valid range).
		if (codeTable.Length < 2)
			return false;

		this.NearSize = codeTable[0];
		this.SameSize = codeTable[1];

		// Modes are bytes: SELF, HERE, near modes and same modes must all fit in 0..255.
		if ((int)VcDiffModes.FIRST + this.NearSize + this.SameSize > (int)VcDiffModes.MAX) return false;

		// The decoded table must be exactly CodeTable.SerializedSize bytes; one spare byte detects a longer output.
		var serialized = this._bytePool.Rent(CodeTable.SerializedSize + 1);
		try
		{
			using var decoder = new VcdiffSpanDecoder(
				new VcdiffDecoderOptions { BytePool = this._bytePool },
				new SequenceSourceReader(new ReadOnlySequence<byte>(CodeTable.DefaultBytes)),
				false);

			var output = serialized.AsSpan(0, CodeTable.SerializedSize + 1);
			var status = decoder.Decode(codeTable.Slice(2), output, out _, out var written, true);
			if (status != OperationStatus.Done)
				return false;

			// The COPY modes of every entry must fit the declared cache sizes.
			if (!CodeTable.TryCreate(output.Slice(0, written), out var table) || !table!.AreModesValid(this.NearSize, this.SameSize))
				return false;

			this.CustomTable = table;
			return true;
		}
		finally
		{
			this._bytePool.Return(serialized);
		}
	}
}
