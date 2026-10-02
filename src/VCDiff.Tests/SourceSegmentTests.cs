using System;
using System.Buffers;
using System.IO;
using VCDiff.Decoders;
using VCDiff.Encoders;
using VCDiff.Shared;
using Xunit;

namespace VCDiff.Tests;

public class SourceSegmentTests
{
	// ------------------------------------------------------------------ helpers

	private static ReadOnlySequence<byte> Seq(byte[] data)
	{
		return new ReadOnlySequence<byte>(data);
	}

	private static byte[] MakeDictionary(int seed = 1, int length = 4096)
	{
		var rnd = new Random(seed);
		var data = new byte[length];
		rnd.NextBytes(data);
		return data;
	}

	// Target starts with a verbatim copy of the dictionary so matches exist against any segment of it.
	private static byte[] MakeTarget(byte[] dict, int seed = 2)
	{
		var rnd = new Random(seed);
		var target = new byte[dict.Length + 16384];
		dict.CopyTo(target, 0);
		rnd.NextBytes(target.AsSpan(dict.Length));

		for (var i = 0; i < 200; i++)
			target[rnd.Next(target.Length)] = (byte)rnd.Next(256);
		return target;
	}

	private static byte[] EncodeStreaming(VcdiffSpanEncoder enc, byte[] target, int inChunk, int outChunk)
	{
		return EncodeWithSegments(enc, target, inChunk, outChunk);
	}

	// Encodes target, applying SetSourceSegment when the number of consumed target bytes reaches "at".
	private static byte[] EncodeWithSegments
	(
		VcdiffSpanEncoder enc,
		byte[] target,
		int inChunk,
		int outChunk,
		params (long at, long offset, long length)[] switches)
	{
		var result = new MemoryStream();
		var outBuf = new byte[outChunk];

		var pos = 0;
		var switchIndex = 0;

		while (pos < target.Length)
		{
			while (switchIndex < switches.Length && switches[switchIndex].at <= pos)
			{
				enc.SetSourceSegment(switches[switchIndex].offset, switches[switchIndex].length);
				switchIndex++;
			}

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
			}
		}

		// Apply any switches that land at or past the end of the target.
		while (switchIndex < switches.Length)
		{
			enc.SetSourceSegment(switches[switchIndex].offset, switches[switchIndex].length);
			switchIndex++;
		}

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

	private static byte[] DecodeStreaming(VcdiffSpanDecoder dec, byte[] delta, int inChunk, int outChunk)
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

	// ------------------------------------------------------------------ round trips

	[Theory, InlineData(64, 64), InlineData(7, 13), InlineData(1024, 5)]
	public void RoundTrip_SegmentSwitches_MidWindow(int inChunk, int outChunk)
	{
		var dict = MakeDictionary();
		var target = MakeTarget(dict);

		// Switch at a non-window boundary (5000 bytes in, while the 1 MiB window is still buffering),
		// then to an overlapping segment, then backwards.
		using var enc = new VcdiffSpanEncoder(Seq(dict));
		var delta = EncodeWithSegments(enc, target, inChunk, outChunk,
			(5000, 0, 3000),
			(9000, 1500, 2500),
			(15000, 500, 2000));

		using var dec = new VcdiffSpanDecoder(Seq(dict));
		var result = DecodeStreaming(dec, delta, inChunk, outChunk);

		Assert.Equal(target, result);
	}

	[Fact]
	public void RoundTrip_SegmentSwitches_WithChecksum_And_LegacyDecoder()
	{
		var dict = MakeDictionary();
		var target = MakeTarget(dict);

		using var enc = new VcdiffSpanEncoder(Seq(dict), new VcdiffEncoderOptions { WindowChecksumFormat = WindowChecksumFormat.Xdelta3 });
		var delta = EncodeWithSegments(enc, target, 512, 512, (4096, 512, 2048), (8192, 0, 4096));

		using (var dec = new VcdiffSpanDecoder(Seq(dict)))
			Assert.Equal(target, DecodeStreaming(dec, delta, 512, 512));

		// The legacy Stream decoder must also accept the segment-relative windows.
		using (var src = new MemoryStream(dict))
		using (var dlt = new MemoryStream(delta))
		using (var output = new MemoryStream())
		using (var dec = new VcdiffDecoder(src, dlt, output))
		{
			Assert.Equal(VcdiffResult.Success, dec.Decode(out _));
			Assert.Equal(target, output.ToArray());
		}
	}

	[Fact]
	public void RoundTrip_ZeroLengthSegment_OmitsSource()
	{
		var dict = MakeDictionary();
		var target = MakeTarget(dict);

		using var enc = new VcdiffSpanEncoder(Seq(dict));
		var delta = EncodeWithSegments(enc, target, 256, 256, (0, 0, 0));

		// The only window must not declare a source segment (RFC 3284 section 4.2): indicator byte 0x00.
		Assert.Equal(0x00, delta[5]);

		using var dec = new VcdiffSpanDecoder(Seq(dict));
		Assert.Equal(target, DecodeStreaming(dec, delta, 256, 256));
	}

	[Fact]
	public void RoundTrip_NonZeroSegment_DeclaresSource()
	{
		var dict = MakeDictionary();
		var target = MakeTarget(dict);

		using var enc = new VcdiffSpanEncoder(Seq(dict));
		var delta = EncodeWithSegments(enc, target, 256, 256, (0, 512, 2048));

		// The first window must declare VCD_SOURCE (0x01); the next two varints are length and position.
		Assert.Equal(0x01, delta[5]);

		using var dec = new VcdiffSpanDecoder(Seq(dict));
		Assert.Equal(target, DecodeStreaming(dec, delta, 256, 256));
	}

	[Fact]
	public void FullDictionarySegment_MatchesDefaultOutput()
	{
		var dict = MakeDictionary();
		var target = MakeTarget(dict);

		using var defaultEnc = new VcdiffSpanEncoder(Seq(dict));
		var defaultDelta = EncodeStreaming(defaultEnc, target, 512, 512);

		using var explicitEnc = new VcdiffSpanEncoder(Seq(dict));
		var explicitDelta = EncodeWithSegments(explicitEnc, target, 512, 512, (0, 0, dict.Length));

		Assert.Equal(defaultDelta, explicitDelta);
	}

	[Theory, InlineData(0.0), InlineData(1.0), InlineData(4.0), InlineData(0.5)]
	public void HashTableSizeMultiplier_RoundTrips(double multiplier)
	{
		var dict = MakeDictionary();
		var target = MakeTarget(dict);

		using var enc = new VcdiffSpanEncoder(Seq(dict), new VcdiffEncoderOptions { HashTableSizeMultiplier = multiplier });
		var delta = EncodeStreaming(enc, target, 512, 512);

		using var dec = new VcdiffSpanDecoder(Seq(dict));
		Assert.Equal(target, DecodeStreaming(dec, delta, 512, 512));
	}

	// ------------------------------------------------------------------ COPY confinement

	[Fact]
	public void Copy_NeverReferencesBytesOutsideSegment()
	{
		var dict = MakeDictionary();
		var target = MakeTarget(dict);

		const long offset = 1024;
		const long length = 2048;

		using var enc = new VcdiffSpanEncoder(Seq(dict));
		var delta = EncodeWithSegments(enc, target, 1024, 1024, (0, offset, length));

		// Corrupt every dictionary byte outside [offset, offset + length). A COPY that strayed outside the
		// segment would copy the corruption into the target and fail this assertion.
		var corrupted = (byte[])dict.Clone();
		for (var i = 0; i < offset; i++)
			corrupted[i] ^= 0xFF;
		for (var i = (int)(offset + length); i < corrupted.Length; i++)
			corrupted[i] ^= 0xFF;

		using var dec = new VcdiffSpanDecoder(Seq(corrupted));
		Assert.Equal(target, DecodeStreaming(dec, delta, 1024, 1024));
	}

	// ------------------------------------------------------------------ argument validation

	[Theory, InlineData(-1, 10), InlineData(0, -1), InlineData(5000, 10), InlineData(4096, 10), InlineData(0, 5000)]
	public void OutOfRangeSegment_Throws(long offset, long length)
	{
		var dict = MakeDictionary();

		using var enc = new VcdiffSpanEncoder(Seq(dict));
		Assert.Throws<ArgumentOutOfRangeException>(() => enc.SetSourceSegment(offset, length));
	}
}
