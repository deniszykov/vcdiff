using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using VCDiff.Decoders;
using VCDiff.Encoders;
using VCDiff.Includes;
using VCDiff.Shared;
using Xunit;

namespace VCDiff.Tests;

/// <summary>
///     The streaming encoder and decoder read the dictionary in place from a segmented
///     <see cref="ReadOnlySequence{T}" />; the result must not depend on how it is split.
/// </summary>
public class SegmentedDictionaryTests
{
	private sealed class Segment : ReadOnlySequenceSegment<byte>
	{
		public Segment(ReadOnlyMemory<byte> memory)
		{
			this.Memory = memory;
		}

		public Segment Append(ReadOnlyMemory<byte> memory)
		{
			var next = new Segment(memory) { RunningIndex = this.RunningIndex + this.Memory.Length };
			this.Next = next;
			return next;
		}
	}

    /// <summary>
    ///     Splits <paramref name="data" /> into separately allocated segments whose sizes cycle through
    ///     <paramref name="sizes" />.
    ///     A size of zero produces an empty segment.
    /// </summary>
    private static ReadOnlySequence<byte> Split(byte[] data, params int[] sizes)
	{
		if (sizes.Length == 0)
			return new ReadOnlySequence<byte>(data);

		Segment first = null;
		Segment last = null;
		var pos = 0;
		for (var i = 0; pos < data.Length; i++)
		{
			var size = Math.Min(sizes[i % sizes.Length], data.Length - pos);
			var copy = data.AsSpan(pos, size).ToArray();
			pos += size;
			if (first == null)
				first = last = new Segment(copy);
			else
				last = last.Append(copy);
		}

		return new ReadOnlySequence<byte>(first, 0, last, last.Memory.Length);
	}

	public static IEnumerable<object[]> Layouts()
	{
		yield return new object[] { Array.Empty<int>() };
		yield return new object[] { new[] { 4096 } };
		yield return new object[] { new[] { 131072 } };
		yield return new object[] { new[] { 1000 } };

		// Segments smaller than a block, empty segments and boundaries inside blocks.
		yield return new object[] { new[] { 1, 7, 0, 15, 17, 33, 1000, 3, 0, 0, 5000 } };
	}

	private static byte[] MakeDictionary(int size, int seed)
	{
		var rnd = new Random(seed);
		var data = new byte[size];
		var pos = 0;
		while (pos < size)
		{
			var len = Math.Min(rnd.Next(16, 600), size - pos);
			switch (rnd.Next(4))
			{
				case 0 when pos > 1000:
					// Repeat earlier content so that several blocks share a hash.
					Array.Copy(data, rnd.Next(pos - len > 0 ? pos - len : 1), data, pos, Math.Min(len, pos));
					break;
				case 1:
					data.AsSpan(pos, len).Fill((byte)rnd.Next(256));
					break;
				default:
					rnd.NextBytes(data.AsSpan(pos, len));
					break;
			}

			pos += len;
		}

		return data;
	}

	private static byte[] MakeTarget(byte[] dict, int size, int seed)
	{
		var rnd = new Random(seed);
		var target = new MemoryStream();
		while (target.Length < size)
		{
			if (rnd.Next(4) == 0)
			{
				var insert = new byte[rnd.Next(1, 300)];
				rnd.NextBytes(insert);
				target.Write(insert);
			}
			else
			{
				var len = rnd.Next(20, 6000);
				var offset = rnd.Next(dict.Length - len);
				var copy = dict.AsSpan(offset, len).ToArray();
				if (rnd.Next(3) == 0)
					copy[rnd.Next(len)] ^= 0x55;
				target.Write(copy);
			}
		}

		return target.ToArray();
	}

	private static byte[] Encode(ReadOnlySequence<byte> dict, byte[] target, VcdiffEncoderOptions options, int inChunk = 50000, int outChunk = 4096)
	{
		using var enc = new VcdiffSpanEncoder(dict, options);
		var result = new MemoryStream();
		var outBuf = new byte[outChunk];
		var input = target.AsSpan();
		while (true)
		{
			var chunk = input.Slice(0, Math.Min(inChunk, input.Length));
			var status = enc.Encode(chunk, outBuf, out var consumed, out var written, chunk.Length == input.Length);
			result.Write(outBuf, 0, written);
			input = input.Slice(consumed);
			if (status == OperationStatus.Done && input.Length == 0)
				break;
		}

		return result.ToArray();
	}

	private static byte[] Decode(ReadOnlySequence<byte> dict, byte[] delta, int inChunk = 50000, int outChunk = 4096)
	{
		using var dec = new VcdiffSpanDecoder(dict);
		var result = new MemoryStream();
		var outBuf = new byte[outChunk];
		var input = delta.AsSpan();
		while (true)
		{
			var chunk = input.Slice(0, Math.Min(inChunk, input.Length));
			var status = dec.Decode(chunk, outBuf, out var consumed, out var written, chunk.Length == input.Length);
			result.Write(outBuf, 0, written);
			input = input.Slice(consumed);
			Assert.NotEqual(OperationStatus.InvalidData, status);
			if (status == OperationStatus.Done)
				break;
		}

		Assert.Equal(0, input.Length);
		return result.ToArray();
	}

	private static byte[] LegacyEncode(byte[] dict, byte[] target, bool interleaved, WindowChecksumFormat checksumFormat, int blockSize)
	{
		using var src = new MemoryStream(dict);
		using var tgt = new MemoryStream(target);
		using var delta = new MemoryStream();
		using var enc = new VcdiffEncoder(src, tgt, delta, blockSize: blockSize);
		Assert.Equal(VcdiffResult.Success, enc.Encode(interleaved, checksumFormat));
		return delta.ToArray();
	}

	private static string Hash(byte[] data)
	{
		return Convert.ToHexString(SHA256.HashData(data));
	}

	// Deltas produced by the contiguous dictionary implementation (before dictionaries could be segmented).
	private static readonly Dictionary<string, string> Golden = new() {
		{ "False/None/16", "43076B0B7EBA0F3AAFA13E849844318B5D2E49CAB3EF6127FB5C9D9B972675F7" },
		{ "False/Sdch/16", "3A09E6CB91FBC611D6382180F550BD75CB7D9F12C5174A4ED54196F201F33A6F" },
		{ "False/Xdelta3/32", "BB69E1BCACE6D50F90FBE2CB84F141F24CEC2C5F5A828AA65C64DB8E8B43EAD6" },
		{ "True/None/16", "BD70E4641D87C1FF2824CB3967CD717F400E0D191C4B6A4F7D28F281BEFFF463" },
		{ "True/Sdch/32", "C2C4A694A27C3DAA16A7A0D23940F9CE18995B9C7B9D7DCF714F691083FD2641" }
	};

	// Deltas produced with HashTableSizeMultiplier = 1 (one bucket per block), the smallest table.
	private static readonly Dictionary<string, string> UnitMultiplierGolden = new() {
		{ "False/Xdelta3/32", "AA602B33F3E6480D105F05AA53C27D4E01FBF03296CED2C196A6D5D1C35D2A0F" },
		{ "True/Sdch/32", "81FB0CE4EE88498F36A22E8D973FF312F18D7ECED106362064C3466EDBE95046" }
	};

	[Theory, InlineData(false, WindowChecksumFormat.None, 16), InlineData(false, WindowChecksumFormat.Sdch, 16), InlineData(false, WindowChecksumFormat.Xdelta3, 32),
	InlineData(true, WindowChecksumFormat.None, 16), InlineData(true, WindowChecksumFormat.Sdch, 32)]
	public void Encoder_Output_DoesNotDependOnSegmentation(bool interleaved, WindowChecksumFormat checksumFormat, int blockSize)
	{
		var dict = MakeDictionary(300_000, 11);
		var target = MakeTarget(dict, 1_300_000, 12);
		var key = $"{interleaved}/{checksumFormat}/{blockSize}";
		Assert.True(Golden.TryGetValue(key, out var golden), $"{{ \"{key}\", \"{Hash(LegacyEncode(dict, target, interleaved, checksumFormat, blockSize))}\" }},");

		Assert.Equal(golden, Hash(LegacyEncode(dict, target, interleaved, checksumFormat, blockSize)));

		foreach (var layout in Layouts())
		{
			var options = new VcdiffEncoderOptions { Interleaved = interleaved, WindowChecksumFormat = checksumFormat, BlockSize = blockSize };
			var delta = Encode(Split(dict, (int[])layout[0]), target, options);
			Assert.Equal(golden, Hash(delta));
		}
	}

	[Theory, InlineData(false, WindowChecksumFormat.Xdelta3, 32), InlineData(true, WindowChecksumFormat.Sdch, 32)]
	public void Encoder_Output_WithUnitHashTableMultiplier(bool interleaved, WindowChecksumFormat checksumFormat, int blockSize)
	{
		var dict = MakeDictionary(300_000, 11);
		var target = MakeTarget(dict, 1_300_000, 12);
		var key = $"{interleaved}/{checksumFormat}/{blockSize}";
		Assert.True(UnitMultiplierGolden.TryGetValue(key, out var golden), $"{{ \"{key}\", \"{Hash(LegacyEncode(dict, target, interleaved, checksumFormat, blockSize))}\" }},");

		// The legacy stream encoder honours HashTableSizeMultiplier through the shared EncoderSession.
		using (var src = new MemoryStream(dict))
		using (var tgt = new MemoryStream(target))
		using (var delta = new MemoryStream())
		using (var enc = new VcdiffEncoder(src, tgt, delta, new VcdiffEncoderOptions { BlockSize = blockSize, HashTableSizeMultiplier = 1 }))
		{
			Assert.Equal(VcdiffResult.Success, enc.Encode(interleaved, checksumFormat));
			Assert.Equal(golden, Hash(delta.ToArray()));
		}

		// The streaming encoder over a segmented dictionary must produce the same delta.
		foreach (var layout in Layouts())
		{
			var options = new VcdiffEncoderOptions { Interleaved = interleaved, WindowChecksumFormat = checksumFormat, BlockSize = blockSize, HashTableSizeMultiplier = 1 };
			var delta = Encode(Split(dict, (int[])layout[0]), target, options);
			Assert.Equal(golden, Hash(delta));
		}
	}

	[Theory, MemberData(nameof(Layouts))]
	public void Decoder_Decodes_WithSegmentedDictionary(int[] layout)
	{
		var dict = MakeDictionary(300_000, 21);
		var target = MakeTarget(dict, 1_300_000, 22);

		foreach (var interleaved in new[] { false, true })
		{
			var options = new VcdiffEncoderOptions { Interleaved = interleaved, WindowChecksumFormat = WindowChecksumFormat.Sdch };
			var delta = Encode(new ReadOnlySequence<byte>(dict), target, options);

			Assert.Equal(target, Decode(Split(dict, layout), delta));
			Assert.Equal(target, Decode(Split(dict, layout), delta, 7, 5000));
		}
	}

	private sealed class TrackingMemoryManager : MemoryManager<byte>
	{
		private readonly byte[] data;
		private GCHandle handle;

		public int Pins { get; private set; }

		public TrackingMemoryManager(byte[] data)
		{
			this.data = data;
		}

		public override Span<byte> GetSpan()
		{
			return this.data;
		}

		public override unsafe MemoryHandle Pin(int elementIndex = 0)
		{
			if (this.Pins++ == 0) this.handle = GCHandle.Alloc(this.data, GCHandleType.Pinned);

			return new MemoryHandle((byte*)this.handle.AddrOfPinnedObject() + elementIndex, default, this);
		}

		public override void Unpin()
		{
			if (--this.Pins == 0) this.handle.Free();
		}

		protected override void Dispose(bool disposing)
		{
		}
	}

	// ------------------------------------------------------------------ bounded buffering

	[Fact]
	public void Decoder_ConsumesInputOnlyAsOutputIsDrained()
	{
		var dict = MakeDictionary(300_000, 51);
		var target = MakeTarget(dict, 3_000_000, 52);
		var rnd = new Random(53);

		// Random data does not match the dictionary, so the delta is about as large as the target.
		rnd.NextBytes(target.AsSpan(100_000, 2_500_000));

		foreach (var interleaved in new[] { false, true })
		{
			var delta = Encode(new ReadOnlySequence<byte>(dict), target, new VcdiffEncoderOptions { Interleaved = interleaved });
			Assert.True(delta.Length > 2_500_000);

			using var dec = new VcdiffSpanDecoder(new ReadOnlySequence<byte>(dict));
			var outBuf = new byte[1024];

			// The whole delta is offered at once, but only a little output space is available.
			var status = dec.Decode(delta, outBuf, out var consumed, out var written, true);
			Assert.Equal(OperationStatus.DestinationTooSmall, status);
			Assert.Equal(outBuf.Length, written);

			// At most one window (1 MiB) plus the internal input buffer may have been taken.
			Assert.True(consumed < 1_200_000, $"consumed {consumed} of {delta.Length}");

			var result = new MemoryStream();
			result.Write(outBuf, 0, written);
			var input = delta.AsSpan(consumed);
			while (status != OperationStatus.Done)
			{
				status = dec.Decode(input, outBuf, out consumed, out written, true);
				Assert.NotEqual(OperationStatus.InvalidData, status);
				Assert.NotEqual(OperationStatus.NeedMoreData, status);
				result.Write(outBuf, 0, written);
				input = input.Slice(consumed);
			}

			Assert.Equal(0, input.Length);
			Assert.Equal(target, result.ToArray());
		}
	}

	[Fact]
	public void Decoder_CorruptInstruction_ReturnsInvalidData()
	{
		var dict = MakeDictionary(50_000, 61);
		var target = MakeTarget(dict, 80_000, 62);

		foreach (var interleaved in new[] { false, true })
		{
			var delta = Encode(new ReadOnlySequence<byte>(dict), target, new VcdiffEncoderOptions { Interleaved = interleaved });
			var rnd = new Random(63);
			var outBuf = new byte[200_000];

			// Whatever is corrupted the decoder must report it (or decode something) without throwing.
			for (var i = 0; i < 300; i++)
			{
				var corrupt = (byte[])delta.Clone();
				corrupt[rnd.Next(5, corrupt.Length)] ^= (byte)rnd.Next(1, 256);

				using var dec = new VcdiffSpanDecoder(new ReadOnlySequence<byte>(dict));
				var status = dec.Decode(corrupt, outBuf, out _, out _, true);
				Assert.True(status == OperationStatus.InvalidData || status == OperationStatus.Done, status.ToString());
			}
		}
	}

	[Fact]
	public void Dictionary_IsReferencedInPlace_WithoutPinning()
	{
		var dict = MakeDictionary(100_000, 31);
		var owner = new TrackingMemoryManager(dict);

		// The reader holds the sequence by reference and reads through GC-safe spans, so it neither copies
		// nor pins the dictionary (pinning was only needed by the old raw-pointer implementation).
		using (var enc = new VcdiffSpanEncoder(new ReadOnlySequence<byte>(owner.Memory))) Assert.Equal(0, owner.Pins);

		Assert.Equal(0, owner.Pins);

		using (var dec = new VcdiffSpanDecoder(new ReadOnlySequence<byte>(owner.Memory))) Assert.Equal(0, owner.Pins);

		Assert.Equal(0, owner.Pins);
	}

	[Fact]
	public void SequenceSourceReader_ReleaseAction_IsInvokedOnceOnDispose()
	{
		var releases = 0;
		var source = new SequenceSourceReader(new ReadOnlySequence<byte>(new byte[16]), () => releases++);

		Assert.Equal(0, releases);
		source.Dispose();
		Assert.Equal(1, releases);

		// Dispose is idempotent: the release action runs exactly once.
		source.Dispose();
		Assert.Equal(1, releases);
	}

	[Fact]
	public void LegacyEncoder_InPlaceDictionary_MatchesCopiedDictionary()
	{
		var dict = MakeDictionary(300_000, 41);
		var target = MakeTarget(dict, 400_000, 42);

		// A read-only MemoryStream is referenced in place, a writable one (LegacyEncode) is copied.
		using var source = new MemoryStream(dict, false);
		using var tgt = new MemoryStream(target);
		using var delta = new MemoryStream();
		using (var enc = new VcdiffEncoder(source, tgt, delta))
			Assert.Equal(VcdiffResult.Success, enc.Encode());

		Assert.Equal(LegacyEncode(dict, target, false, WindowChecksumFormat.None, 16), delta.ToArray());
	}
}