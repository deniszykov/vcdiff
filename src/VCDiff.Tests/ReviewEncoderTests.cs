using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.IO;
using VCDiff.Decoders;
using VCDiff.Encoders;
using VCDiff.Includes;
using VCDiff.Shared;
using Xunit;

namespace VCDiff.Tests;

public class ReviewEncoderTests
{
	private const int MiB = 1024 * 1024;

	// ------------------------------------------------------------------ helpers

	private static byte[] MakeDictionary(int length, int seed = 11)
	{
		var data = new byte[length];
		new Random(seed).NextBytes(data);
		return data;
	}

	// The dictionary repeated (so every window finds matches) with sparse random edits.
	private static byte[] MakeTarget(byte[] dict, int length, int seed = 12)
	{
		var rnd = new Random(seed);
		var target = new byte[length];
		for (var i = 0; i < length; i++)
		{
			target[i] = dict[(i * 7 / 8 + 13) % dict.Length];
			if (rnd.Next(64) == 0) target[i] = (byte)rnd.Next(256);
		}

		return target;
	}

	private static byte[] EncodeBaseline(byte[] dict, byte[] target, VcEncoderOptions options, bool interleaved = false, ChecksumFormat checksum = ChecksumFormat.None)
	{
		using var tgt = new MemoryStream(target);
		using var delta = new MemoryStream();
		using var enc = new VcEncoder(new MemoryStream(dict, false), tgt, delta, options);
		Assert.Equal(VcDiffResult.SUCCESS, enc.Encode(interleaved, checksum));
		return delta.ToArray();
	}

	private static byte[] EncodeWith(Stream source, Stream target, VcEncoderOptions options, bool interleaved = false, ChecksumFormat checksum = ChecksumFormat.None)
	{
		using var delta = new MemoryStream();
		using var enc = new VcEncoder(source, target, delta, options);
		Assert.Equal(VcDiffResult.SUCCESS, enc.Encode(interleaved, checksum));
		return delta.ToArray();
	}

	private static byte[] Decode(byte[] dict, byte[] delta)
	{
		using var src = new MemoryStream(dict);
		using var dlt = new MemoryStream(delta);
		using var output = new MemoryStream();
		using var dec = new VcDecoder(src, dlt, output);
		Assert.Equal(VcDiffResult.SUCCESS, dec.Decode(out _));
		return output.ToArray();
	}

	private static int ReadVarint(byte[] data, ref int pos)
	{
		var result = 0;
		while (true)
		{
			var b = data[pos++];
			result = (result << 7) | (b & 0x7F);
			if ((b & 0x80) == 0) return result;
		}
	}

	// Walks the window headers of a delta and returns the decoded target length of every window.
	private static int[] WindowTargetLengths(byte[] delta)
	{
		var lengths = new System.Collections.Generic.List<int>();
		var pos = 5; // magic (4) + header indicator (no secondary compressor / code table)
		while (pos < delta.Length)
		{
			var indicator = delta[pos++];
			if ((indicator & 0x03) != 0)
			{
				ReadVarint(delta, ref pos); // source segment length
				ReadVarint(delta, ref pos); // source segment position
			}

			var deltaLength = ReadVarint(delta, ref pos);
			var deltaStart = pos;
			lengths.Add(ReadVarint(delta, ref pos));
			pos = deltaStart + deltaLength;
		}

		Assert.Equal(delta.Length, pos);
		return lengths.ToArray();
	}

	private static RecyclableMemoryStreamManager SmallBlockManager()
	{
		// An odd block size, so the dictionary is multi segment and blocks straddle segment boundaries.
		return new RecyclableMemoryStreamManager(new RecyclableMemoryStreamManager.Options { BlockSize = 1000 });
	}

	// A seekable stream that returns at most 1-3 bytes per Read.
	private sealed class TrickleStream : Stream
	{
		private readonly MemoryStream inner;
		private int next;

		public TrickleStream(byte[] data)
		{
			this.inner = new MemoryStream(data, false);
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

		private int Limit(int count)
		{
			this.next = this.next % 3 + 1;
			return Math.Min(count, this.next);
		}

		public override int Read(byte[] buffer, int offset, int count)
		{
			return this.inner.Read(buffer, offset, this.Limit(count));
		}

		public override int Read(Span<byte> buffer)
		{
			return this.inner.Read(buffer.Slice(0, this.Limit(buffer.Length)));
		}

		public override long Seek(long offset, SeekOrigin origin)
		{
			return this.inner.Seek(offset, origin);
		}

		public override void Flush()
		{
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

	// ------------------------------------------------------------------ source in place

	public static TheoryData<int, bool, ChecksumFormat> SourceCases => new() {
		{ 16, false, ChecksumFormat.None },
		{ 16, true, ChecksumFormat.SDCH },
		{ 16, false, ChecksumFormat.Xdelta3 },
		{ 512, false, ChecksumFormat.SDCH } // straddle buffer larger than the stack limit
	};

	[Theory, MemberData(nameof(SourceCases))]
	public void SourceStreamVariants_ProduceIdenticalDeltas(int blockSize, bool interleaved, ChecksumFormat checksum)
	{
		var dict = MakeDictionary(64 * 1024 + 123);
		var target = MakeTarget(dict, 200 * 1024 + 7);
		var options = new VcEncoderOptions { BlockSize = blockSize, ChunkSize = blockSize * 2 };
		var expected = EncodeBaseline(dict, target, options, interleaved, checksum);
		Assert.Equal(target, Decode(dict, expected));

		const int prefix = 37;
		var padded = new byte[prefix + dict.Length + 19];
		new Random(5).NextBytes(padded);
		Buffer.BlockCopy(dict, 0, padded, prefix, dict.Length);

		// Not exposable: copied.
		using (var src = new MemoryStream(dict))
		{
			Assert.Equal(expected, EncodeWith(src, new MemoryStream(target), options, interleaved, checksum));
			Assert.Equal(src.Length, src.Position);
		}

		// Exposable, non-zero origin and non-zero position.
		using (var src = new MemoryStream(padded, 3, prefix - 3 + dict.Length, false, true))
		{
			src.Position = prefix - 3;
			Assert.Equal(expected, EncodeWith(src, new MemoryStream(target), options, interleaved, checksum));
			Assert.Equal(src.Length, src.Position);
		}

		// Growable MemoryStream (exposable) at a non-zero position.
		using (var src = new MemoryStream())
		{
			src.Write(padded, 0, prefix + dict.Length);
			src.Position = prefix;
			Assert.Equal(expected, EncodeWith(src, new MemoryStream(target), options, interleaved, checksum));
			Assert.Equal(src.Length, src.Position);
		}

		// RecyclableMemoryStream, multi segment, at a non-zero position.
		var manager = SmallBlockManager();
		using (var src = manager.GetStream())
		{
			src.Write(padded, 0, prefix + dict.Length);
			src.Position = prefix;
			Assert.Equal(expected, EncodeWith(src, new MemoryStream(target), options, interleaved, checksum));
			Assert.Equal(src.Length, src.Position);
		}

		// Non-memory stream returning short reads: copied into pooled blocks.
		using (var src = new TrickleStream(padded.AsSpan(0, prefix + dict.Length).ToArray()))
		{
			src.Position = prefix;
			Assert.Equal(expected, EncodeWith(src, new MemoryStream(target), options, interleaved, checksum));
			Assert.Equal(src.Length, src.Position);
		}
	}

	[Fact]
	public void SourceAtEnd_IsEmptyDictionary()
	{
		var dict = MakeDictionary(4096);
		var target = MakeTarget(dict, 4096);
		using var src = new MemoryStream();
		src.Write(dict, 0, dict.Length); // Position == Length
		using var delta = new MemoryStream();
		using var enc = new VcEncoder(src, new MemoryStream(target), delta);
		Assert.Equal(VcDiffResult.ERROR, enc.Encode());
	}

	// ------------------------------------------------------------------ window boundaries

	[Theory]
	[InlineData(1, 1)]
	[InlineData(1, 2)]
	[InlineData(1, 1000)]
	[InlineData(1, MiB - 1)]
	[InlineData(1, MiB)]
	[InlineData(1, MiB + 1)]
	[InlineData(1, 3 * MiB + 5)]
	[InlineData(2, 1)]
	[InlineData(2, 2)]
	[InlineData(2, 2 * MiB)]
	[InlineData(2, 2 * MiB + 1)]
	[InlineData(0, MiB + 1)] // <= 0 means 1 MiB
	public void TargetWindows_DoNotDependOnReadGranularity(int maxBufferSize, int targetLength)
	{
		var dict = MakeDictionary(32 * 1024);
		var target = MakeTarget(dict, targetLength);
		var options = new VcEncoderOptions { MaxBufferSize = maxBufferSize };

		var expected = EncodeBaseline(dict, target, options);

		var window = Math.Max(1, maxBufferSize) * MiB;
		var windows = WindowTargetLengths(expected);
		Assert.Equal((targetLength + window - 1) / window, windows.Length);
		for (var i = 0; i < windows.Length; i++)
			Assert.Equal(Math.Min(window, targetLength - i * window), windows[i]);

		using (var trickle = new TrickleStream(target))
			Assert.Equal(expected, EncodeWith(new MemoryStream(dict), trickle, options));

		Assert.Equal(target, Decode(dict, expected));
	}

	[Theory, InlineData(MiB + 1), InlineData(5)]
	public async Task EncodeAsync_TrickleTarget_MatchesSync(int targetLength)
	{
		var dict = MakeDictionary(32 * 1024);
		var target = MakeTarget(dict, targetLength);
		var options = new VcEncoderOptions();
		var expected = EncodeBaseline(dict, target, options, false, ChecksumFormat.SDCH);

		using var trickle = new TrickleStream(target);
		using var delta = new MemoryStream();
		using var enc = new VcEncoder(new MemoryStream(dict), trickle, delta, options);
		Assert.Equal(VcDiffResult.SUCCESS, await enc.EncodeAsync(false, ChecksumFormat.SDCH));
		Assert.Equal(expected, delta.ToArray());
	}

	[Fact]
	public void TargetNotAtStart_IsEncodedFromStart()
	{
		var dict = MakeDictionary(8 * 1024);
		var target = MakeTarget(dict, 10 * 1024);
		var options = new VcEncoderOptions();
		var expected = EncodeBaseline(dict, target, options);

		using var tgt = new MemoryStream(target);
		tgt.Position = 100;
		Assert.Equal(expected, EncodeWith(new MemoryStream(dict), tgt, options));
	}

	[Fact]
	public void MaxBufferSize_Overflow_Throws()
	{
		// 2048 MiB overflows the int window size; it must not silently degrade to tiny windows.
		Assert.Throws<ArgumentOutOfRangeException>(() =>
			new VcEncoder(new MemoryStream(new byte[16]), new MemoryStream(new byte[16]), new MemoryStream(), new VcEncoderOptions { MaxBufferSize = 2048 }));
	}

	// ------------------------------------------------------------------ lifetime

	[Fact]
	public void Encode_Twice_And_DoubleDispose()
	{
		var dict = MakeDictionary(8 * 1024);
		var target = MakeTarget(dict, 20 * 1024);
		var manager = SmallBlockManager();
		using var src = manager.GetStream();
		src.Write(dict, 0, dict.Length);
		src.Position = 0;

		using var tgt = new MemoryStream(target);
		using var delta1 = new MemoryStream();
		var enc = new VcEncoder(src, tgt, delta1, new VcEncoderOptions());
		Assert.Equal(VcDiffResult.SUCCESS, enc.Encode());
		var first = delta1.ToArray();

		delta1.SetLength(0);
		Assert.Equal(VcDiffResult.SUCCESS, enc.Encode());
		Assert.Equal(first, delta1.ToArray());

		enc.Dispose();
		enc.Dispose();
		Assert.Equal(target, Decode(dict, first));
	}

	[Fact]
	public void CallerOwnedRollingHash_IsNotDisposed()
	{
		var dict = MakeDictionary(8 * 1024);
		var target = MakeTarget(dict, 20 * 1024);
		using var hasher = new RollingHash(16);
		byte[] first;
		using (var delta = new MemoryStream())
		using (var enc = new VcEncoder(new MemoryStream(dict), new MemoryStream(target), delta, new VcEncoderOptions { RollingHash = hasher }))
		{
			Assert.Equal(VcDiffResult.SUCCESS, enc.Encode());
			first = delta.ToArray();
		}

		using (var delta = new MemoryStream())
		using (var enc = new VcEncoder(new MemoryStream(dict), new MemoryStream(target), delta, new VcEncoderOptions { RollingHash = hasher }))
		{
			Assert.Equal(VcDiffResult.SUCCESS, enc.Encode());
			Assert.Equal(first, delta.ToArray());
		}
	}
}
