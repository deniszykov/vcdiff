using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using VCDiff.Decoders;
using VCDiff.Encoders;
using VCDiff.Includes;
using VCDiff.Shared;
using Xunit;

namespace VCDiff.Tests;

public class StreamingDiffTests
{
	// ------------------------------------------------------------------ helpers

	private static ReadOnlySequence<byte> Seq(byte[] data)
	{
		return new ReadOnlySequence<byte>(data);
	}

	private static byte[] MakeDictionary(int seed = 1)
	{
		var rnd = new Random(seed);
		var data = new byte[4096];
		rnd.NextBytes(data);
		return data;
	}

	private static byte[] MakeTarget(byte[] dict, int seed = 2)
	{
		var rnd = new Random(seed);
		var target = new byte[dict.Length + 16384];
		dict.CopyTo(target, 0);
		rnd.NextBytes(target.AsSpan(dict.Length));

		// Scatter some edits to exercise ADD + COPY mixes.
		for (var i = 0; i < 200; i++)
			target[rnd.Next(target.Length)] = (byte)rnd.Next(256);
		return target;
	}

	private static byte[] EncodeStreaming(VcDiffEncoder enc, byte[] target, int inChunk, int outChunk)
	{
		var result = new MemoryStream();
		var outBuf = new byte[outChunk];

		var pos = 0;
		while (pos < target.Length)
		{
			var take = Math.Min(inChunk, target.Length - pos);
			var input = target.AsSpan(pos, take);

			while (input.Length > 0)
			{
				var status = enc.Encode(input, outBuf, out var ic, out var ow, false);
				result.Write(outBuf, 0, ow);
				input = input.Slice(ic);
				pos += ic;

				if (status == OperationStatus.InvalidData) throw new InvalidOperationException("encode failed");

				if (status == OperationStatus.DestinationTooSmall) continue;

				if (status == OperationStatus.NeedMoreData) break;

				// Done: continue with the remaining input in this chunk.
			}
		}

		// Final flush.
		while (true)
		{
			var status = enc.Encode(ReadOnlySpan<byte>.Empty, outBuf, out var ic, out var ow, true);
			result.Write(outBuf, 0, ow);
			if (status == OperationStatus.InvalidData) throw new InvalidOperationException("encode final failed");
			if (status == OperationStatus.NeedMoreData) throw new InvalidOperationException("unexpected NeedMoreData during final flush");

			if (status == OperationStatus.Done) break;
		}

		return result.ToArray();
	}

	private static byte[] DecodeStreaming(VcDiffDecoder dec, byte[] delta, int inChunk, int outChunk)
	{
		var result = new MemoryStream();
		var outBuf = new byte[outChunk];

		var pos = 0;
		while (pos < delta.Length)
		{
			var take = Math.Min(inChunk, delta.Length - pos);
			var input = delta.AsSpan(pos, take);
			pos += take;

			OperationStatus status;
			int ic, ow;
			do
			{
				status = dec.Decode(input, outBuf, out ic, out ow, false);
				result.Write(outBuf, 0, ow);
				input = input.Slice(ic);
			} while (status == OperationStatus.DestinationTooSmall);

			if (status == OperationStatus.InvalidData) throw new InvalidOperationException("decode failed");
		}

		// Final flush: drain remaining output and confirm completion.
		while (true)
		{
			var status = dec.Decode(ReadOnlySpan<byte>.Empty, outBuf, out var ic, out var ow, true);
			result.Write(outBuf, 0, ow);
			if (status == OperationStatus.InvalidData) throw new InvalidOperationException("decode final failed");
			if (status == OperationStatus.NeedMoreData) throw new InvalidOperationException("unexpected NeedMoreData during final decode");

			if (status == OperationStatus.Done) break;
		}

		return result.ToArray();
	}

	private static byte[] LegacyEncode(byte[] dict, byte[] target, bool interleaved, ChecksumFormat checksumFormat, int maxBufferSize = 1, int blockSize = 16)
	{
		using var src = new MemoryStream(dict);
		using var tgt = new MemoryStream(target);
		using var delta = new MemoryStream();
		using var enc = new VcEncoder(src, tgt, delta, maxBufferSize, blockSize);
		Assert.Equal(VcDiffResult.SUCCESS, enc.Encode(interleaved, checksumFormat));
		return delta.ToArray();
	}

	private static byte[] LegacyDecode(byte[] dict, byte[] delta)
	{
		using var src = new MemoryStream(dict);
		using var dlt = new MemoryStream(delta);
		using var output = new MemoryStream();
		using var dec = new VcDecoder(src, dlt, output);
		Assert.Equal(VcDiffResult.SUCCESS, dec.Decode(out _));
		return output.ToArray();
	}

	// ------------------------------------------------------------------ round trips

	[Theory, InlineData(1, 1), InlineData(7, 13), InlineData(512, 512), InlineData(4096, 1), InlineData(1, 4096)]
	public void RoundTrip_NoChecksum(int inChunk, int outChunk)
	{
		var dict = MakeDictionary();
		var target = MakeTarget(dict);

		using var enc = new VcDiffEncoder(Seq(dict));
		var delta = EncodeStreaming(enc, target, inChunk, outChunk);

		using var dec = new VcDiffDecoder(Seq(dict));
		var result = DecodeStreaming(dec, delta, inChunk, outChunk);

		Assert.Equal(target, result);
	}

	[Theory, InlineData(3, 5), InlineData(256, 256)]
	public void RoundTrip_SdchChecksum(int inChunk, int outChunk)
	{
		var dict = MakeDictionary();
		var target = MakeTarget(dict);

		using var enc = new VcDiffEncoder(Seq(dict), new VcEncoderOptions { ChecksumFormat = ChecksumFormat.SDCH });
		var delta = EncodeStreaming(enc, target, inChunk, outChunk);

		using var dec = new VcDiffDecoder(Seq(dict));
		var result = DecodeStreaming(dec, delta, inChunk, outChunk);

		Assert.Equal(target, result);
	}

	[Theory, InlineData(3, 5), InlineData(256, 256)]
	public void RoundTrip_Xdelta3Checksum(int inChunk, int outChunk)
	{
		var dict = MakeDictionary();
		var target = MakeTarget(dict);

		using var enc = new VcDiffEncoder(Seq(dict), new VcEncoderOptions { ChecksumFormat = ChecksumFormat.Xdelta3 });
		var delta = EncodeStreaming(enc, target, inChunk, outChunk);

		using var dec = new VcDiffDecoder(Seq(dict));
		var result = DecodeStreaming(dec, delta, inChunk, outChunk);

		Assert.Equal(target, result);
	}

	[Theory, InlineData(3, 5), InlineData(256, 256)]
	public void RoundTrip_Interleaved(int inChunk, int outChunk)
	{
		var dict = MakeDictionary();
		var target = MakeTarget(dict);

		using var enc = new VcDiffEncoder(Seq(dict), new VcEncoderOptions { Interleaved = true });
		var delta = EncodeStreaming(enc, target, inChunk, outChunk);

		using var dec = new VcDiffDecoder(Seq(dict));
		var result = DecodeStreaming(dec, delta, inChunk, outChunk);

		Assert.Equal(target, result);
	}

	[Theory, InlineData("patch.openvcdiff"), InlineData("checksum.openvcdiff"), InlineData("interleaved.openvcdiff"), InlineData("checksum_interleaved.openvcdiff"),
	InlineData("sample.xdelta"), InlineData("sample_nosmallstr.xdelta"), InlineData("sample_appheader.xdelta"), InlineData("a-to-b-lzma-compression.xdelta")]
	public void Decoder_Decodes_ExternalPatches(string patchFile)
	{
		var dict = File.ReadAllBytes(Path.Combine("patches", "a.test"));
		var expected = File.ReadAllBytes(Path.Combine("patches", "b.test"));
		var delta = File.ReadAllBytes(Path.Combine("patches", patchFile));

		using var dec = new VcDiffDecoder(Seq(dict));
		var result = DecodeStreaming(dec, delta, 64, 64);

		Assert.Equal(expected, result);
	}

	private static IEnumerable<byte[]> Chunk(byte[] data, int size)
	{
		for (var i = 0; i < data.Length; i += size)
		{
			var len = Math.Min(size, data.Length - i);
			var buf = new byte[len];
			Array.Copy(data, i, buf, 0, len);
			yield return buf;
		}
	}

	[Fact]
	public void Decoder_Decodes_WinIndicatorZeroPatch()
	{
		var dict = File.ReadAllBytes(Path.Combine("patches", "empty.test"));
		var expected = File.ReadAllBytes(Path.Combine("patches", "win_indicator_zero.test"));
		var delta = File.ReadAllBytes(Path.Combine("patches", "win_indicator_zero.xdelta"));

		using var dec = new VcDiffDecoder(Seq(dict));
		var result = DecodeStreaming(dec, delta, 64, 64);

		Assert.Equal(expected, result);
	}

	[Fact]
	public void Decoder_Matches_LegacyDecoder()
	{
		var dict = MakeDictionary();
		var target = MakeTarget(dict);

		var delta = LegacyEncode(dict, target, false, ChecksumFormat.SDCH);
		using var dec = new VcDiffDecoder(Seq(dict));
		var result = DecodeStreaming(dec, delta, 128, 128);

		Assert.Equal(target, result);
	}

	// ------------------------------------------------------------------ error handling

	[Fact]
	public void Decoder_TruncatedDelta_ReturnsInvalidData()
	{
		var dict = MakeDictionary();
		var target = MakeTarget(dict);

		using var enc = new VcDiffEncoder(Seq(dict));
		var delta = EncodeStreaming(enc, target, 1024, 1024);

		// Feed a truncated delta and mark it final.
		var truncated = delta.AsSpan(0, delta.Length / 2).ToArray();
		using var dec = new VcDiffDecoder(Seq(dict));
		var outBuf = new byte[1024];
		var status = OperationStatus.Done;
		foreach (var chunk in Chunk(truncated, 32))
		{
			status = dec.Decode(chunk, outBuf, out _, out _, false);
			if (status == OperationStatus.InvalidData)
				break;
		}

		if (status != OperationStatus.InvalidData) status = dec.Decode(ReadOnlySpan<byte>.Empty, outBuf, out _, out _, true);

		Assert.Equal(OperationStatus.InvalidData, status);
	}

	// ------------------------------------------------------------------ cross compatibility

	[Fact]
	public void Encoder_Matches_LegacyEncoder()
	{
		var dict = MakeDictionary();
		var target = MakeTarget(dict);

		using var enc = new VcDiffEncoder(Seq(dict));
		var delta = EncodeStreaming(enc, target, 512, 512);
		var expected = LegacyEncode(dict, target, false, ChecksumFormat.None);

		Assert.Equal(expected, delta);
	}

	// ------------------------------------------------------------------ dictionary loading

	[Fact]
	public void ReadDictionary_ProducesSequence_And_RoundTrips()
	{
		var dict = MakeDictionary();
		var target = MakeTarget(dict);

		using var src = new MemoryStream(dict);
		using var dictStream = VcDiff.ReadDictionary(src);
		var sequence = dictStream.GetReadOnlySequence();

		Assert.Equal(dict, sequence.ToArray());

		using var enc = new VcDiffEncoder(sequence);
		var delta = EncodeStreaming(enc, target, 512, 512);

		using var dec = new VcDiffDecoder(sequence);
		var result = DecodeStreaming(dec, delta, 512, 512);

		Assert.Equal(target, result);
	}

	[Fact]
	public void RoundTrip_EmptyTarget()
	{
		var dict = MakeDictionary();

		using var enc = new VcDiffEncoder(Seq(dict));
		var delta = EncodeStreaming(enc, Array.Empty<byte>(), 1, 1);

		using var dec = new VcDiffDecoder(Seq(dict));
		var result = DecodeStreaming(dec, delta, 1, 1);

		Assert.Empty(result);
	}

	// ------------------------------------------------------------------ multi-window

	[Fact]
	public void RoundTrip_MultiWindow()
	{
		var rnd = new Random(42);
		var dict = new byte[8192];
		rnd.NextBytes(dict);

		var target = new byte[3 * 1024 * 1024];
		dict.CopyTo(target, 0);
		rnd.NextBytes(target.AsSpan(dict.Length));

		using var enc = new VcDiffEncoder(Seq(dict), new VcEncoderOptions { ChecksumFormat = ChecksumFormat.SDCH });
		var delta = EncodeStreaming(enc, target, 70000, 70000);

		using var dec = new VcDiffDecoder(Seq(dict));
		var result = DecodeStreaming(dec, delta, 70000, 70000);

		Assert.Equal(target, result);
	}

	[Fact]
	public void StreamingEncode_LegacyDecode()
	{
		var dict = MakeDictionary();
		var target = MakeTarget(dict);

		using var enc = new VcDiffEncoder(Seq(dict), new VcEncoderOptions { ChecksumFormat = ChecksumFormat.Xdelta3 });
		var delta = EncodeStreaming(enc, target, 1024, 1024);
		var result = LegacyDecode(dict, delta);

		Assert.Equal(target, result);
	}
}