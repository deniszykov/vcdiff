using System;
using System.Buffers;
using System.IO;
using VCDiff.Decoders;
using VCDiff.Encoders;
using VCDiff.Includes;
using VCDiff.Shared;
using Xunit;

namespace VCDiff.Tests;

// Adversarial streaming tests: random per-call input/output sizes (including empty spans and spans
// larger than a window, so both the in-place and the staged paths are taken, and switched between,
// mid-stream), segmented dictionaries, all checksum formats and both layouts.
public class ReviewStreamingTests
{
	private const int Window = 1024 * 1024;

	private static readonly int[] TargetLengths = { 0, 1, 100, Window - 1, Window, Window + 1, 2 * Window, 2 * Window + 12345 };

	private sealed class Segment : ReadOnlySequenceSegment<byte>
	{
		public Segment(ReadOnlyMemory<byte> memory, Segment previous)
		{
			this.Memory = memory;
			if (previous != null)
			{
				this.RunningIndex = previous.RunningIndex + previous.Memory.Length;
				previous.Next = this;
			}
		}
	}

	private static ReadOnlySequence<byte> RandomSegmented(byte[] data, Random rnd)
	{
		Segment first = null, last = null;
		var pos = 0;
		while (pos < data.Length)
		{
			var len = Math.Min(rnd.Next(3) == 0 ? rnd.Next(1, 8) : rnd.Next(1, 3000), data.Length - pos);
			last = new Segment(data.AsMemory(pos, len), last);
			first ??= last;
			pos += len;
		}

		return first == null ? ReadOnlySequence<byte>.Empty : new ReadOnlySequence<byte>(first, 0, last, last.Memory.Length);
	}

	private static int PickSize(Random rnd)
	{
		switch (rnd.Next(10))
		{
			case 0: return 0;
			case 1:
			case 2:
			case 3:
			case 4: return rnd.Next(1, 17);
			case 5:
			case 6:
			case 7: return rnd.Next(1, 65537);
			case 8: return rnd.Next(1, Window + 2);
			default: return 3 * Window;
		}
	}

	private static byte[] EncodeRandom(VcDiffEncoder enc, byte[] target, Random rnd)
	{
		var result = new MemoryStream();
		var pos = 0;
		var idle = 0;
		while (true)
		{
			var take = Math.Min(PickSize(rnd), target.Length - pos);
			var isFinal = pos + take == target.Length;
			var output = new byte[PickSize(rnd)];

			// Poison the caller's input once the call returns: the encoder must not keep using it.
			var input = target.AsSpan(pos, take).ToArray();
			var status = enc.Encode(input, output, out var ic, out var ow, isFinal);
			Array.Fill(input, (byte)0xAA);

			Assert.NotEqual(OperationStatus.InvalidData, status);
			result.Write(output, 0, ow);
			pos += ic;

			if (status == OperationStatus.Done && isFinal && pos == target.Length && ic == 0 && ow == 0)
				break;

			idle = ic == 0 && ow == 0 ? idle + 1 : 0;
			Assert.True(idle < 1000, "encoder made no progress");
		}

		return result.ToArray();
	}

	private static byte[] DecodeRandom(VcDiffDecoder dec, byte[] delta, Random rnd)
	{
		var result = new MemoryStream();
		var pos = 0;
		var idle = 0;
		while (true)
		{
			var take = Math.Min(PickSize(rnd), delta.Length - pos);
			var isFinal = pos + take == delta.Length;
			var output = new byte[PickSize(rnd)];

			var input = delta.AsSpan(pos, take).ToArray();
			var status = dec.Decode(input, output, out var ic, out var ow, isFinal);
			Array.Fill(input, (byte)0x55);

			Assert.NotEqual(OperationStatus.InvalidData, status);
			result.Write(output, 0, ow);
			pos += ic;

			if (status == OperationStatus.Done)
			{
				Assert.Equal(delta.Length, pos);
				break;
			}

			idle = ic == 0 && ow == 0 ? idle + 1 : 0;
			Assert.True(idle < 1000, "decoder made no progress");
		}

		return result.ToArray();
	}

	// Reference: target fed in small fixed chunks, so every window is staged.
	private static byte[] EncodeStaged(VcDiffEncoder enc, byte[] target)
	{
		var result = new MemoryStream();
		var output = new byte[1 << 16];
		var pos = 0;
		while (true)
		{
			var take = Math.Min(4096, target.Length - pos);
			var isFinal = pos + take == target.Length;
			var status = enc.Encode(target.AsSpan(pos, take), output, out var ic, out var ow, isFinal);
			Assert.NotEqual(OperationStatus.InvalidData, status);
			result.Write(output, 0, ow);
			pos += ic;
			if (status == OperationStatus.Done && isFinal && pos == target.Length && ic == 0 && ow == 0)
				break;
		}

		return result.ToArray();
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

	public static TheoryData<int, bool, ChecksumFormat> Cases()
	{
		var data = new TheoryData<int, bool, ChecksumFormat>();
		for (var seed = 1; seed <= 20; seed++)
		{
			switch (seed % 4)
			{
				case 0: data.Add(seed, false, ChecksumFormat.None); break;
				case 1: data.Add(seed, false, ChecksumFormat.SDCH); break;
				case 2: data.Add(seed, false, ChecksumFormat.Xdelta3); break;
				default: data.Add(seed, true, ChecksumFormat.SDCH); break;
			}
		}

		return data;
	}

	// External patches carry target-to-target COPYs, xdelta3 checksums and secondary compression,
	// which the library's own encoder never produces; random output sizes decode them both in place
	// and staged.
	[Theory, InlineData("patch.openvcdiff"), InlineData("checksum.openvcdiff"), InlineData("interleaved.openvcdiff"), InlineData("checksum_interleaved.openvcdiff"),
	InlineData("sample.xdelta"), InlineData("sample_nosmallstr.xdelta"), InlineData("sample_appheader.xdelta"), InlineData("a-to-b-lzma-compression.xdelta")]
	public void ExternalPatches_RandomChunks(string patchFile)
	{
		var dict = File.ReadAllBytes(Path.Combine("patches", "a.test"));
		var expected = File.ReadAllBytes(Path.Combine("patches", "b.test"));
		var delta = File.ReadAllBytes(Path.Combine("patches", patchFile));

		for (var seed = 1; seed <= 20; seed++)
		{
			var rnd = new Random(seed);
			var dictionary = seed % 2 == 0 ? RandomSegmented(dict, rnd) : new ReadOnlySequence<byte>(dict);
			using var dec = new VcDiffDecoder(dictionary);
			Assert.Equal(expected, DecodeRandom(dec, delta, rnd));
		}
	}

	[Theory, MemberData(nameof(Cases))]
	public void RandomChunks_RoundTrip_MatchesStaged(int seed, bool interleaved, ChecksumFormat checksumFormat)
	{
		var rnd = new Random(seed);
		var dict = new byte[8192];
		rnd.NextBytes(dict);

		var target = new byte[TargetLengths[seed % TargetLengths.Length]];
		for (var i = 0; i < target.Length; i += dict.Length)
			dict.AsSpan(0, Math.Min(dict.Length, target.Length - i)).CopyTo(target.AsSpan(i));
		for (var i = 0; i < target.Length / 400; i++)
			target[rnd.Next(target.Length)] = (byte)rnd.Next(256);

		var options = new VcEncoderOptions { Interleaved = interleaved, ChecksumFormat = checksumFormat };

		using var staged = new VcDiffEncoder(new ReadOnlySequence<byte>(dict), options);
		var expected = EncodeStaged(staged, target);

		var dictionary = seed % 2 == 0 ? RandomSegmented(dict, rnd) : new ReadOnlySequence<byte>(dict);
		using var enc = new VcDiffEncoder(dictionary, options);
		var delta = EncodeRandom(enc, target, rnd);
		Assert.Equal(expected, delta);

		Assert.Equal(target, LegacyDecode(dict, delta));

		using var dec = new VcDiffDecoder(dictionary);
		Assert.Equal(target, DecodeRandom(dec, delta, rnd));
	}
}
