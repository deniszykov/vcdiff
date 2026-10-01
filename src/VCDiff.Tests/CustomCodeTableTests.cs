#nullable enable
using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using VCDiff.Decoders;
using VCDiff.Includes;
using VCDiff.Shared;
using Xunit;

namespace VCDiff.Tests;

// Deltas that carry an application-defined code table (RFC 3284 section 7, Hdr_Indicator VCD_CODETABLE).
// Everything is hand-assembled: the header holds [length of code table data][near][same][VCDIFF delta of the
// 1536-byte custom table against the 1536-byte default table], and the window instructions use opcodes whose
// meaning differs between the custom and the default table, so decoding is only correct if the table is honored.
public class CustomCodeTableTests
{
	private const byte RUN = 2, ADD = 1, COPY = 3, NOOP = 0;

	private static readonly byte[] Dictionary = Encoding.ASCII.GetBytes("0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz");

	// ---------------------------------------------------------------- default table serialization

	[Fact]
	public void DefaultTable_SerializesInRfcOrder()
	{
		var bytes = CodeTable.DefaultBytes.Span;
		Assert.Equal(1536, bytes.Length);

		// inst1: opcode 0 = RUN, 1 = ADD, 19 = COPY, 163 = ADD, 247 = COPY
		Assert.Equal(RUN, bytes[0]);
		Assert.Equal(ADD, bytes[1]);
		Assert.Equal(COPY, bytes[19]);
		Assert.Equal(ADD, bytes[163]);
		Assert.Equal(COPY, bytes[247]);
		// inst2 row: NOOP for single opcodes, COPY for 163, ADD for 247
		Assert.Equal(NOOP, bytes[256 + 1]);
		Assert.Equal(COPY, bytes[256 + 163]);
		Assert.Equal(ADD, bytes[256 + 247]);
		// size1 row: opcode 2 = ADD size 1, opcode 18 = size 17
		Assert.Equal(1, bytes[512 + 2]);
		Assert.Equal(17, bytes[512 + 18]);
		// size2 row: opcode 163 = 4, opcode 247 = 1
		Assert.Equal(4, bytes[768 + 163]);
		Assert.Equal(1, bytes[768 + 247]);
		// mode1 row: opcode 35 = mode 1, opcode 255 = mode 8
		Assert.Equal(1, bytes[1024 + 35]);
		Assert.Equal(8, bytes[1024 + 255]);
		// mode2 row: opcode 175 = mode 1, opcode 246 = mode 8
		Assert.Equal(1, bytes[1280 + 175]);
		Assert.Equal(8, bytes[1280 + 246]);

		Assert.True(CodeTable.DefaultTable.Inst1.SequenceEqual(bytes.Slice(0, 256)));
		Assert.True(CodeTable.DefaultTable.Mode2.SequenceEqual(bytes.Slice(1280, 256)));
	}

	// ---------------------------------------------------------------- swapped opcodes

	// Custom table: opcode 0 and opcode 1 swapped => 0 = ADD (explicit size), 1 = RUN (explicit size).
	private static byte[] SwappedTable()
	{
		var table = CodeTable.DefaultBytes.ToArray();
		for (var row = 0; row < 6; row++)
		{
			var o = row * 256;
			(table[o], table[o + 1]) = (table[o + 1], table[o]);
		}

		return table;
	}

	// ADD "abc" then RUN 4 x 'z' when read with the swapped table.
	private static byte[] SwappedWindow()
	{
		var data = Encoding.ASCII.GetBytes("abcz");
		var inst = new byte[] { 0x00, 3, 0x01, 4 };
		return Window(sourceLength: 0, targetLength: 7, data, inst, Array.Empty<byte>());
	}

	private static readonly byte[] SwappedExpected = Encoding.ASCII.GetBytes("abczzzz");

	[Fact]
	public void SwappedOpcodes_LegacyDecoder_UsesCustomTable()
	{
		var delta = Delta(CustomTableSection(SwappedTable(), 4, 3), SwappedWindow());
		Assert.Equal(SwappedExpected, DecodeLegacy(delta));
	}

	[Fact]
	public async Task SwappedOpcodes_LegacyDecoderAsync_UsesCustomTable()
	{
		var delta = Delta(CustomTableSection(SwappedTable(), 4, 3), SwappedWindow());
		using var source = new MemoryStream(Dictionary);
		using var deltaStream = new MemoryStream(delta);
		using var output = new MemoryStream();
		using (var decoder = new VcDecoder(source, deltaStream, output))
		{
			var (result, written) = await decoder.DecodeAsync();
			Assert.Equal(VcDiffResult.SUCCESS, result);
			Assert.Equal(SwappedExpected.Length, written);
		}

		Assert.Equal(SwappedExpected, output.ToArray());
	}

	[Theory, InlineData(1), InlineData(3), InlineData(int.MaxValue)]
	public void SwappedOpcodes_StreamingDecoder_UsesCustomTable(int inChunk)
	{
		var delta = Delta(CustomTableSection(SwappedTable(), 4, 3), SwappedWindow());
		Assert.Equal(SwappedExpected, DecodeStreaming(delta, inChunk));
	}

	[Fact]
	public void SwappedOpcodes_SameInstructionsWithDefaultTable_DecodeDifferently()
	{
		// Sanity check of the fixture: without the custom table the same window must not produce the expected target.
		var delta = Delta(null, SwappedWindow());
		Assert.NotEqual(SwappedExpected, TryDecodeLegacy(delta));
		Assert.NotEqual(SwappedExpected, TryDecodeStreaming(delta));
	}

	// ---------------------------------------------------------------- near / same cache sizes

	// Custom table with near = 1, same = 1: modes are SELF(0), HERE(1), NEAR0(2), SAME0(3). Entries using modes > 3
	// are invalid for these cache sizes, so they are remapped to SELF; everything else is the default table.
	private static byte[] SmallCacheTable()
	{
		var table = CodeTable.DefaultBytes.ToArray();
		for (var op = 0; op < 256; op++)
		{
			if (table[1024 + op] > 3) table[1024 + op] = 0;
			if (table[1280 + op] > 3) table[1280 + op] = 0;
		}

		return table;
	}

	// Dictionary: 0-9 at 0..9, A-Z at 10..35, a-z at 36..61.
	// COPY 4 SELF @30 (opcode 19), COPY 4 SELF @5, COPY 4 mode 2 (opcode 51) +1, COPY 4 mode 3 (opcode 67) byte 30.
	//   near=1/same=1: mode 2 = NEAR0 -> near[0] (5) + 1 = 6; mode 3 = SAME0 -> same[30] = 30.
	//   near=4/same=3: mode 2 = NEAR0 -> near[0] (30) + 1 = 31; mode 3 = NEAR1 -> near[1] (5) + 30 = 35.
	private static byte[] SmallCacheWindow()
	{
		var inst = new byte[] { 19, 4, 19, 4, 51, 4, 67, 4 };
		var addr = new byte[] { 30, 5, 1, 30 };
		return Window(sourceLength: Dictionary.Length, targetLength: 16, Array.Empty<byte>(), inst, addr);
	}

	private static readonly byte[] SmallCacheExpected = Encoding.ASCII.GetBytes("UVWX" + "5678" + "6789" + "UVWX");

	private static readonly byte[] DefaultCacheExpected = Encoding.ASCII.GetBytes("UVWX" + "5678" + "VWXY" + "Zabc");

	[Fact]
	public void SmallCaches_LegacyDecoder_HonorsNearAndSameSizes()
	{
		var delta = Delta(CustomTableSection(SmallCacheTable(), 1, 1), SmallCacheWindow());
		Assert.Equal(SmallCacheExpected, DecodeLegacy(delta));
	}

	[Theory, InlineData(1), InlineData(int.MaxValue)]
	public void SmallCaches_StreamingDecoder_HonorsNearAndSameSizes(int inChunk)
	{
		var delta = Delta(CustomTableSection(SmallCacheTable(), 1, 1), SmallCacheWindow());
		Assert.Equal(SmallCacheExpected, DecodeStreaming(delta, inChunk));
	}

	[Fact]
	public void SmallCaches_SameWindowWithDefaultCaches_DecodesDifferently()
	{
		// Sanity check of the fixture: the same instructions under the default table / cache sizes.
		var delta = Delta(null, SmallCacheWindow());
		Assert.Equal(DefaultCacheExpected, DecodeLegacy(delta));
		Assert.Equal(DefaultCacheExpected, DecodeStreaming(delta, 1));
	}

	// Custom table with near = 0, same = 0 (allowed by RFC 3284 and accepted by open-vcdiff / xdelta3): only SELF(0)
	// and HERE(1) exist, so every COPY entry using a cache mode is remapped to SELF.
	private static byte[] NoCacheTable()
	{
		var table = CodeTable.DefaultBytes.ToArray();
		for (var op = 0; op < 256; op++)
		{
			if (table[1024 + op] > 1) table[1024 + op] = 0;
			if (table[1280 + op] > 1) table[1280 + op] = 0;
		}

		return table;
	}

	// COPY 4 SELF @30 (opcode 19), COPY 4 HERE (opcode 35): here = 62 + 4 = 66, 66 - 56 = 10 ("ABCD").
	private static byte[] NoCacheWindow()
	{
		var inst = new byte[] { 19, 4, 35, 4 };
		var addr = new byte[] { 30, 56 };
		return Window(sourceLength: Dictionary.Length, targetLength: 8, Array.Empty<byte>(), inst, addr);
	}

	[Theory, InlineData(1), InlineData(int.MaxValue)]
	public void ZeroCacheSizes_AreAccepted(int inChunk)
	{
		var delta = Delta(CustomTableSection(NoCacheTable(), 0, 0), NoCacheWindow());
		var expected = Encoding.ASCII.GetBytes("UVWX" + "ABCD");
		Assert.Equal(expected, DecodeLegacy(delta));
		Assert.Equal(expected, DecodeStreaming(delta, inChunk));
	}

	// ---------------------------------------------------------------- validation

	[Fact]
	public void CustomTable_WithModeBeyondCacheSizes_IsRejected()
	{
		// the unmodified default table uses modes up to 8, which do not exist with near = 1, same = 1
		var delta = Delta(CustomTableSection(CodeTable.DefaultBytes.ToArray(), 1, 1), SwappedWindow());
		Assert.Null(TryDecodeLegacy(delta));
		Assert.Null(TryDecodeStreaming(delta));
	}

	[Fact]
	public void CustomTable_WithInvalidInstructionType_IsRejected()
	{
		var table = SwappedTable();
		table[5] = 7; // inst1 of opcode 5: not NOOP/ADD/RUN/COPY
		var delta = Delta(CustomTableSection(table, 4, 3), SwappedWindow());
		Assert.Null(TryDecodeLegacy(delta));
		Assert.Null(TryDecodeStreaming(delta));
	}

	[Fact]
	public void CustomTable_WithWrongDecodedLength_IsRejected()
	{
		var delta = Delta(CustomTableSection(SwappedTable().AsSpan(0, 1000).ToArray(), 4, 3), SwappedWindow());
		Assert.Null(TryDecodeLegacy(delta));
		Assert.Null(TryDecodeStreaming(delta));
	}

	// ---------------------------------------------------------------- assembling helpers

	private static void WriteVarInt(List<byte> output, long value)
	{
		Span<byte> tmp = stackalloc byte[10];
		var n = 0;
		do
		{
			tmp[n++] = (byte)(value & 0x7F);
			value >>= 7;
		} while (value != 0);

		for (var i = n - 1; i >= 0; i--)
			output.Add((byte)(tmp[i] | (i > 0 ? 0x80 : 0)));
	}

	// A window (RFC 3284 section 4.2) with VCD_SOURCE over [0, sourceLength) when sourceLength > 0.
	private static byte[] Window(int sourceLength, int targetLength, byte[] data, byte[] inst, byte[] addr)
	{
		var body = new List<byte>();
		WriteVarInt(body, targetLength);
		body.Add(0); // Delta_Indicator
		WriteVarInt(body, data.Length);
		WriteVarInt(body, inst.Length);
		WriteVarInt(body, addr.Length);
		body.AddRange(data);
		body.AddRange(inst);
		body.AddRange(addr);

		var w = new List<byte>();
		if (sourceLength > 0)
		{
			w.Add(0x01); // VCD_SOURCE
			WriteVarInt(w, sourceLength);
			WriteVarInt(w, 0);
		}
		else
			w.Add(0x00);

		WriteVarInt(w, body.Count);
		w.AddRange(body);
		return w.ToArray();
	}

	// Full delta: header (with the code table section when given) followed by the window.
	private static byte[] Delta(byte[]? codeTableSection, byte[] window)
	{
		var d = new List<byte> { 0xD6, 0xC3, 0xC4, 0x00, (byte)(codeTableSection != null ? 0x02 : 0x00) };
		if (codeTableSection != null) d.AddRange(codeTableSection);
		d.AddRange(window);
		return d.ToArray();
	}

	// [Length of code table data][near][same][VCDIFF delta of table vs. default table bytes]
	private static byte[] CustomTableSection(byte[] table, byte near, byte same)
	{
		var embedded = EncodeAgainstDefault(table);
		var section = new List<byte>();
		WriteVarInt(section, 2 + embedded.Length);
		section.Add(near);
		section.Add(same);
		section.AddRange(embedded);
		return section.ToArray();
	}

	// Encodes the table as a complete VCDIFF delta (own header, default code table) against the default table bytes:
	// equal runs become COPY (SELF mode, opcode 19) from the source segment, differing runs become ADD (opcode 1).
	// Using COPYs means a wrong dictionary (e.g. an all-zero default table) produces a wrong custom table.
	private static byte[] EncodeAgainstDefault(byte[] table)
	{
		var dict = CodeTable.DefaultBytes.Span;
		var data = new List<byte>();
		var inst = new List<byte>();
		var addr = new List<byte>();

		var pos = 0;
		while (pos < table.Length)
		{
			var start = pos;
			if (pos < dict.Length && table[pos] == dict[pos])
			{
				while (pos < table.Length && pos < dict.Length && table[pos] == dict[pos]) pos++;
				inst.Add(19);
				WriteVarInt(inst, pos - start);
				WriteVarInt(addr, start);
			}
			else
			{
				while (pos < table.Length && !(pos < dict.Length && table[pos] == dict[pos])) pos++;
				inst.Add(1);
				WriteVarInt(inst, pos - start);
				for (var i = start; i < pos; i++) data.Add(table[i]);
			}
		}

		return Delta(null, Window(dict.Length, table.Length, data.ToArray(), inst.ToArray(), addr.ToArray()));
	}

	// ---------------------------------------------------------------- decoding helpers

	private static byte[] DecodeLegacy(byte[] delta)
	{
		var result = TryDecodeLegacy(delta);
		Assert.NotNull(result);
		return result!;
	}

	private static byte[]? TryDecodeLegacy(byte[] delta)
	{
		using var source = new MemoryStream(Dictionary);
		using var deltaStream = new MemoryStream(delta);
		using var output = new MemoryStream();
		try
		{
			using var decoder = new VcDecoder(source, deltaStream, output);
			if (decoder.Decode(out var written) != VcDiffResult.SUCCESS) return null;
			Assert.Equal(output.Length, written);
		}
		catch (Exception e) when (e is not Xunit.Sdk.XunitException)
		{
			return null;
		}

		return output.ToArray();
	}

	private static byte[] DecodeStreaming(byte[] delta, int inChunk)
	{
		var result = TryDecodeStreaming(delta, inChunk);
		Assert.NotNull(result);
		return result!;
	}

	private static byte[]? TryDecodeStreaming(byte[] delta, int inChunk = int.MaxValue)
	{
		using var decoder = new VcDiffDecoder(new ReadOnlySequence<byte>(Dictionary));
		var result = new MemoryStream();
		var outBuf = new byte[16];

		var pos = 0;
		while (true)
		{
			var take = (int)Math.Min(inChunk, (long)delta.Length - pos);
			var input = delta.AsSpan(pos, take);
			pos += take;
			var isFinal = pos == delta.Length;

			OperationStatus status;
			do
			{
				status = decoder.Decode(input, outBuf, out var consumed, out var written, isFinal);
				result.Write(outBuf, 0, written);
				input = input.Slice(consumed);
			} while (status == OperationStatus.DestinationTooSmall);

			if (status == OperationStatus.InvalidData) return null;
			if (isFinal) return status == OperationStatus.Done ? result.ToArray() : null;
		}
	}
}
