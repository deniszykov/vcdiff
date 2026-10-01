using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using VCDiff.Decoders;
using VCDiff.Encoders;
using VCDiff.Includes;
using VCDiff.Shared;
using Xunit;

namespace VCDiff.Tests;

// Review tests for the Stream based decoder (VcDecoder): streams that hand out 1-3 bytes per Read (which also drives
// the on-demand Seek+Read dictionary path), in-memory and non-seekable dictionaries, truncated deltas, pool hygiene
// (dirty, oversized pooled arrays; every rent returned exactly once) and double Dispose.
public class ReviewDecoderTests
{
	public static IEnumerable<object[]> Patches()
	{
		foreach (var patch in new[] {
					"checksum.openvcdiff", "checksum_interleaved.openvcdiff", "patch.openvcdiff", "interleaved.openvcdiff", "sample.xdelta",
					"sample_nosmallstr.xdelta", "sample_appheader.xdelta", "a-to-b-lzma-compression.xdelta"
				})
		foreach (var maxChunk in new[] { 1, 2, 3 })
			yield return new object[] { patch, maxChunk };
	}

	private static byte[] ReadPatchFile(string name)
	{
		return File.ReadAllBytes($"patches{Path.DirectorySeparatorChar}{name}");
	}

	[Theory, MemberData(nameof(Patches))]
	public void LegacyDecoder_SmallChunkStreams_ProducesTarget(string patchFile, int maxChunk)
	{
		var expected = ReadPatchFile("b.test");
		using var source = new ChunkedStream(ReadPatchFile("a.test"), maxChunk);
		using var delta = new ChunkedStream(ReadPatchFile(patchFile), maxChunk);
		using var output = new MemoryStream();

		using (var decoder = new VcDecoder(source, delta, output))
		{
			Assert.Equal(VcDiffResult.SUCCESS, decoder.Decode(out var written));
			Assert.Equal(expected.Length, written);
		}

		Assert.Equal(expected, output.ToArray());
	}

	[Theory, MemberData(nameof(Patches))]
	public async Task LegacyDecoder_SmallChunkStreams_ProducesTargetAsync(string patchFile, int maxChunk)
	{
		var expected = ReadPatchFile("b.test");
		using var source = new ChunkedStream(ReadPatchFile("a.test"), maxChunk);
		using var delta = new ChunkedStream(ReadPatchFile(patchFile), maxChunk);
		using var output = new MemoryStream();

		using (var decoder = new VcDecoder(source, delta, output))
		{
			var (result, written) = await decoder.DecodeAsync();
			Assert.Equal(VcDiffResult.SUCCESS, result);
			Assert.Equal(expected.Length, written);
		}

		Assert.Equal(expected, output.ToArray());
	}

	[Theory, InlineData("patch.openvcdiff"), InlineData("interleaved.openvcdiff"), InlineData("checksum_interleaved.openvcdiff"),
	InlineData("sample_appheader.xdelta"), InlineData("a-to-b-lzma-compression.xdelta")]
	public void LegacyDecoder_DirtyTrackingPool_AllRentsReturnedOnce(string patchFile)
	{
		var expected = ReadPatchFile("b.test");
		var pool = new TrackingPool();

		using var source = new ChunkedStream(ReadPatchFile("a.test"), 3);
		using var delta = new ChunkedStream(ReadPatchFile(patchFile), 3);
		using var output = new MemoryStream();

		var decoder = new VcDecoder(source, delta, output, new VcDecoderOptions { BytePool = pool });
		Assert.Equal(VcDiffResult.SUCCESS, decoder.Decode(out _));
		decoder.Dispose();
		decoder.Dispose();

		Assert.Equal(expected, output.ToArray());
		Assert.Equal(0, pool.Outstanding);
	}

	[Fact]
	public void LegacyDecoder_DisposeTwice_WithoutDecoding()
	{
		var pool = new TrackingPool();
		using var source = new MemoryStream(ReadPatchFile("a.test"));
		using var delta = new MemoryStream(ReadPatchFile("patch.openvcdiff"));
		using var output = new MemoryStream();

		var decoder = new VcDecoder(source, delta, output, new VcDecoderOptions { BytePool = pool });
		decoder.Dispose();
		decoder.Dispose();

		Assert.Equal(0, pool.Outstanding);
	}

	// Overlapping target COPY (a repeating pattern) encoded by this library, decoded through 1-byte reads.
	[Theory, InlineData(1, false, ChecksumFormat.None), InlineData(3, false, ChecksumFormat.Xdelta3), InlineData(1, true, ChecksumFormat.SDCH),
	InlineData(2, true, ChecksumFormat.SDCH)]
	public void LegacyDecoder_RepeatingTarget_SmallChunks(int maxChunk, bool interleaved, ChecksumFormat checksumFormat)
	{
		var target = new byte[40_000];
		for (var i = 0; i < target.Length; i++) target[i] = (byte)("abcdefg"[i % 7] + i / 5000);

		var dict = new byte[1000];
		new Random(42).NextBytes(dict);

		using var deltaStream = new MemoryStream();
		using (var encoder = new VcEncoder(new MemoryStream(dict), new MemoryStream(target), deltaStream))
		{
			Assert.Equal(VcDiffResult.SUCCESS, encoder.Encode(interleaved, checksumFormat));
		}

		using var output = new MemoryStream();
		using (var decoder = new VcDecoder(new ChunkedStream(dict, maxChunk), new ChunkedStream(deltaStream.ToArray(), maxChunk), output))
		{
			Assert.Equal(VcDiffResult.SUCCESS, decoder.Decode(out _));
		}

		Assert.Equal(target, output.ToArray());
	}

	public static IEnumerable<object[]> DictionaryKinds()
	{
		foreach (var patch in new[] { "patch.openvcdiff", "interleaved.openvcdiff", "a-to-b-lzma-compression.xdelta" })
		foreach (var kind in new[] { "readonly-memory", "exposable-memory", "recyclable", "chunked", "non-seekable" })
			yield return new object[] { patch, kind };
	}

	[Theory, MemberData(nameof(DictionaryKinds))]
	public async Task LegacyDecoder_DictionaryStreamKinds_ProduceTarget(string patchFile, string kind)
	{
		var expected = ReadPatchFile("b.test");
		var dict = ReadPatchFile("a.test");
		foreach (var useAsync in new[] { false, true })
		{
			var pool = new TrackingPool();
			using var source = OpenDictionary(dict, kind);
			using var delta = new MemoryStream(ReadPatchFile(patchFile), false);
			using var output = new MemoryStream();

			var decoder = new VcDecoder(source, delta, output, new VcDecoderOptions { BytePool = pool });
			var (result, written) = useAsync ? await decoder.DecodeAsync() : (decoder.Decode(out var w), w);
			Assert.Equal(VcDiffResult.SUCCESS, result);
			Assert.Equal(expected.Length, written);
			decoder.Dispose();

			Assert.Equal(expected, output.ToArray());
			Assert.Equal(0, pool.Outstanding);
		}
	}

	private static Stream OpenDictionary(byte[] dict, string kind)
	{
		switch (kind)
		{
			case "readonly-memory":
				return new MemoryStream(dict, false);
			case "exposable-memory":
				var exposable = new MemoryStream();
				exposable.Write(dict, 0, dict.Length);
				return exposable;
			case "recyclable":
				var recyclable = new Microsoft.IO.RecyclableMemoryStreamManager().GetStream();
				recyclable.Write(dict, 0, dict.Length);
				return recyclable;
			case "chunked":
				return new ChunkedStream(dict, 7);
			default:
				return new NonSeekableStream(dict);
		}
	}

	// Every proper prefix of a delta is truncated: decoding reports EOD (never loops, never throws, never SUCCESS).
	[Theory, InlineData("patch.openvcdiff"), InlineData("interleaved.openvcdiff"), InlineData("checksum_interleaved.openvcdiff"),
	InlineData("sample.xdelta"), InlineData("a-to-b-lzma-compression.xdelta")]
	public void LegacyDecoder_TruncatedDelta_ReturnsEod(string patchFile)
	{
		var dict = ReadPatchFile("a.test");
		var delta = ReadPatchFile(patchFile);
		var step = Math.Max(1, delta.Length / 97);
		for (var length = 0; length < delta.Length; length += step)
		{
			using var output = new MemoryStream();
			using var decoder = new VcDecoder(new MemoryStream(dict, false), new MemoryStream(delta, 0, length, false), output);
			Assert.Equal(VcDiffResult.EOD, decoder.Decode(out var written));
			Assert.Equal(output.Length, written);
		}
	}

	[Fact]
	public void LegacyDecoder_CorruptDelta_ReturnsError()
	{
		var delta = ReadPatchFile("patch.openvcdiff");
		delta[0] ^= 0xFF;
		using var output = new MemoryStream();
		using var decoder = new VcDecoder(new MemoryStream(ReadPatchFile("a.test")), new MemoryStream(delta), output);
		Assert.Equal(VcDiffResult.ERROR, decoder.Decode(out _));
	}

	[Fact]
	public async Task LegacyDecoder_SecondDecode_ReturnsEod()
	{
		using var output = new MemoryStream();
		using var decoder = new VcDecoder(new MemoryStream(ReadPatchFile("a.test")), new MemoryStream(ReadPatchFile("patch.openvcdiff")), output);
		Assert.Equal(VcDiffResult.SUCCESS, decoder.Decode(out _));
		Assert.Equal(VcDiffResult.EOD, decoder.Decode(out var written));
		Assert.Equal(0, written);
		Assert.Equal((VcDiffResult.EOD, 0L), await decoder.DecodeAsync());
	}

	[Fact]
	public void LegacyDecoder_DisableChecksums_IgnoresWrongInterleavedChecksum()
	{
		// Changing a dictionary byte that the delta copies breaks the checksum of the decoded target.
		var dict = ReadPatchFile("a.test");
		for (var i = 0; i < dict.Length; i += 64) dict[i] ^= 0x5A;
		var delta = ReadPatchFile("checksum_interleaved.openvcdiff");

		using (var output = new MemoryStream())
		using (var decoder = new VcDecoder(new MemoryStream(dict), new MemoryStream(delta), output))
		{
			Assert.Equal(VcDiffResult.ERROR, decoder.Decode(out _));
		}

		using (var output = new MemoryStream())
		using (var decoder = new VcDecoder(new MemoryStream(dict), new MemoryStream(delta), output, disableChecksums: true))
		{
			Assert.Equal(VcDiffResult.SUCCESS, decoder.Decode(out _));
		}
	}

	// Stream that can only be read forward.
	private sealed class NonSeekableStream : Stream
	{
		private readonly MemoryStream inner;

		public NonSeekableStream(byte[] data)
		{
			this.inner = new MemoryStream(data, false);
		}

		public override bool CanRead => true;
		public override bool CanSeek => false;
		public override bool CanWrite => false;
		public override long Length => throw new NotSupportedException();

		public override long Position
		{
			get => throw new NotSupportedException();
			set => throw new NotSupportedException();
		}

		public override void Flush()
		{
		}

		public override int Read(byte[] buffer, int offset, int count)
		{
			return this.inner.Read(buffer, offset, Math.Min(count, 5));
		}

		public override long Seek(long offset, SeekOrigin origin)
		{
			throw new NotSupportedException();
		}

		public override void SetLength(long value)
		{
			throw new NotSupportedException();
		}

		public override void Write(byte[] buffer, int offset, int count)
		{
			throw new NotSupportedException();
		}
	}

	// Seekable stream that returns at most maxChunk bytes per Read.
	private sealed class ChunkedStream : Stream
	{
		private readonly MemoryStream inner;
		private readonly int maxChunk;

		public ChunkedStream(byte[] data, int maxChunk)
		{
			this.inner = new MemoryStream(data, false);
			this.maxChunk = maxChunk;
		}

		public override bool CanRead => true;
		public override bool CanSeek => true;
		public override bool CanWrite => false;
		public override long Length => this.inner.Length;

		public override long Position
		{
			get => this.inner.Position;
			set => this.inner.Position = value;
		}

		public override void Flush()
		{
		}

		public override int Read(byte[] buffer, int offset, int count)
		{
			return this.inner.Read(buffer, offset, Math.Min(count, this.maxChunk));
		}

		public override int Read(Span<byte> buffer)
		{
			return this.inner.Read(buffer.Slice(0, Math.Min(buffer.Length, this.maxChunk)));
		}

		public override long Seek(long offset, SeekOrigin origin)
		{
			return this.inner.Seek(offset, origin);
		}

		public override void SetLength(long value)
		{
			throw new NotSupportedException();
		}

		public override void Write(byte[] buffer, int offset, int count)
		{
			throw new NotSupportedException();
		}
	}

	// Hands out dirty, oversized arrays and fails on returning an array that is not outstanding.
	private sealed class TrackingPool : ArrayPool<byte>
	{
		private readonly HashSet<byte[]> outstanding = new(ReferenceEqualityComparer.Instance);

		public int Outstanding
		{
			get
			{
				lock (this.outstanding) return this.outstanding.Count;
			}
		}

		public override byte[] Rent(int minimumLength)
		{
			var array = new byte[minimumLength + 17];
			array.AsSpan().Fill(0xCD);
			lock (this.outstanding) this.outstanding.Add(array);
			return array;
		}

		public override void Return(byte[] array, bool clearArray = false)
		{
			lock (this.outstanding)
			{
				if (!this.outstanding.Remove(array))
					throw new InvalidOperationException("Array returned twice or not rented from this pool.");
			}

			array.AsSpan().Fill(0xEE);
		}
	}
}
