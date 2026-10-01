#nullable enable
using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using VCDiff.Compression;
using VCDiff.Compressors;
using VCDiff.Decoders;
using VCDiff.Includes;
using VCDiff.Shared;
using Xunit;

namespace VCDiff.Tests;

/// <summary>
///     Review tests for the allocation refactor of the embedded XZ/LZMA2 decoder (secondary compression).
/// </summary>
public class ReviewCompressionTests
{
	private const string LZMA_PATCH = "a-to-b-lzma-compression.xdelta";

	private static string PatchPath(string name)
	{
		return $"patches{Path.DirectorySeparatorChar}{name}";
	}

	// ------------------------------------------------------------------ ReadOnlySequenceStream

	private sealed class Segment : ReadOnlySequenceSegment<byte>
	{
		public Segment(ReadOnlyMemory<byte> memory, Segment? previous)
		{
			this.Memory = memory;
			if (previous != null)
			{
				this.RunningIndex = previous.RunningIndex + previous.Memory.Length;
				previous.Next = this;
			}
		}
	}

	/// <summary>Builds a multi-segment sequence, cutting <paramref name="data" /> at the given sizes (0-length segments allowed).</summary>
	private static ReadOnlySequence<byte> MultiSegment(byte[] data, IEnumerable<int> sizes)
	{
		Segment? first = null, last = null;
		var pos = 0;
		foreach (var size in sizes)
		{
			var len = Math.Min(size, data.Length - pos);
			last = new Segment(data.AsMemory(pos, len), last);
			first ??= last;
			pos += len;
			if (pos >= data.Length) break;
		}

		if (pos < data.Length)
		{
			last = new Segment(data.AsMemory(pos), last);
			first ??= last;
		}

		if (first == null) return ReadOnlySequence<byte>.Empty;

		return new ReadOnlySequence<byte>(first, 0, last!, last!.Memory.Length);
	}

	private static IEnumerable<int> RandomSizes(Random rnd, int maxSize)
	{
		while (true) yield return rnd.Next(0, maxSize + 1);
		// ReSharper disable once IteratorNeverReturns
	}

	[Theory, InlineData(1), InlineData(7), InlineData(1000), InlineData(12345)]
	public void SequenceStream_RandomOps_MatchMemoryStream(int seed)
	{
		var rnd = new Random(seed);
		var data = new byte[rnd.Next(0, 5000)];
		rnd.NextBytes(data);

		var sequence = MultiSegment(data, RandomSizes(rnd, 64));
		Assert.Equal(data.Length, sequence.Length);

		using var oracle = new MemoryStream(data, false);
		using var subject = new ReadOnlySequenceStream(sequence);

		Assert.Equal(oracle.Length, subject.Length);
		for (var op = 0; op < 3000; op++)
		{
			switch (rnd.Next(7))
			{
				case 0:
				{
					var count = rnd.Next(0, 200);
					var a = new byte[count + 4];
					var b = new byte[count + 4];
					var ra = oracle.Read(a, 2, count);
					var rb = subject.Read(b, 2, count);
					Assert.Equal(ra, rb);
					Assert.Equal(a, b);
					break;
				}
				case 1:
				{
					var count = rnd.Next(0, 200);
					var a = new byte[count];
					var b = new byte[count];
					var ra = oracle.Read(a.AsSpan());
					var rb = subject.Read(b.AsSpan());
					Assert.Equal(ra, rb);
					Assert.Equal(a, b);
					break;
				}
				case 2:
					Assert.Equal(oracle.ReadByte(), subject.ReadByte());
					break;
				case 3:
				{
					var target = rnd.Next(0, data.Length + 50);
					oracle.Position = target;
					subject.Position = target;
					break;
				}
				case 4:
				{
					var offset = rnd.Next(-60, 60);
					if (oracle.Position + offset < 0) offset = -(int)oracle.Position;
					Assert.Equal(oracle.Seek(offset, SeekOrigin.Current), subject.Seek(offset, SeekOrigin.Current));
					break;
				}
				case 5:
				{
					var offset = -rnd.Next(0, Math.Max(1, data.Length + 1));
					if (data.Length + offset < 0) offset = -data.Length;
					if (rnd.Next(4) == 0) offset = rnd.Next(0, 20); // beyond end
					Assert.Equal(oracle.Seek(offset, SeekOrigin.End), subject.Seek(offset, SeekOrigin.End));
					break;
				}
				default:
				{
					var offset = rnd.Next(0, data.Length + 20);
					Assert.Equal(oracle.Seek(offset, SeekOrigin.Begin), subject.Seek(offset, SeekOrigin.Begin));
					break;
				}
			}

			Assert.Equal(oracle.Position, subject.Position);
		}
	}

	[Fact]
	public void SequenceStream_EmptyAndZeroLengthSegments()
	{
		using var empty = new ReadOnlySequenceStream(ReadOnlySequence<byte>.Empty);
		Assert.Equal(0, empty.Length);
		Assert.Equal(-1, empty.ReadByte());
		Assert.Equal(0, empty.Read(new byte[10], 0, 10));
		Assert.Equal(0, empty.Position);
		empty.Position = 5;
		Assert.Equal(-1, empty.ReadByte());
		Assert.Equal(5, empty.Position);

		var data = new byte[] { 1, 2, 3, 4, 5, 6 };
		using var s = new ReadOnlySequenceStream(MultiSegment(data, new[] { 0, 1, 0, 0, 2, 0, 3 }));
		var buf = new byte[10];
		Assert.Equal(6, s.Read(buf, 0, 10));
		Assert.Equal(data, buf.Take(6).ToArray());
		Assert.Equal(-1, s.ReadByte());

		// Position set into the middle of a segment.
		s.Position = 4;
		Assert.Equal(5, s.ReadByte());
		Assert.Equal(6, s.ReadByte());
		Assert.Equal(-1, s.ReadByte());

		Assert.Throws<IOException>(() => s.Seek(-1, SeekOrigin.Begin));
		Assert.Throws<ArgumentOutOfRangeException>(() => s.Position = -1);
	}

	[Fact]
	public void SequenceStream_ResetReuse()
	{
		var a = Enumerable.Range(0, 100).Select(i => (byte)i).ToArray();
		var b = Enumerable.Range(0, 37).Select(i => (byte)(255 - i)).ToArray();

		using var s = new ReadOnlySequenceStream(MultiSegment(a, new[] { 3, 5, 7 }));
		var buf = new byte[50];
		Assert.Equal(50, s.Read(buf, 0, 50));

		s.Reset(MultiSegment(b, new[] { 1, 2 }));
		Assert.Equal(0, s.Position);
		Assert.Equal(b.Length, s.Length);
		var all = new byte[100];
		Assert.Equal(b.Length, s.Read(all, 0, 100));
		Assert.Equal(b, all.Take(b.Length).ToArray());

		s.Reset(ReadOnlySequence<byte>.Empty);
		Assert.Equal(0, s.Length);
		Assert.Equal(-1, s.ReadByte());
	}

	// ------------------------------------------------------------------ end-to-end xz decoding

	/// <summary>Returns at most 1-3 bytes per read.</summary>
	private sealed class TrickleStream : Stream
	{
		private readonly Stream _inner;
		private readonly Random _rnd;

		public TrickleStream(Stream inner, int seed)
		{
			this._inner = inner;
			this._rnd = new Random(seed);
		}

		public override bool CanRead => true;
		public override bool CanSeek => this._inner.CanSeek;
		public override bool CanWrite => false;
		public override long Length => this._inner.Length;
		public override long Position { get => this._inner.Position; set => this._inner.Position = value; }

		public override int Read(byte[] buffer, int offset, int count)
		{
			return this._inner.Read(buffer, offset, Math.Min(count, this._rnd.Next(1, 4)));
		}

		public override int Read(Span<byte> buffer)
		{
			return this._inner.Read(buffer.Slice(0, Math.Min(buffer.Length, this._rnd.Next(1, 4))));
		}

		public override void Flush()
		{
		}

		public override long Seek(long offset, SeekOrigin origin)
		{
			return this._inner.Seek(offset, origin);
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

	private static readonly Lazy<(byte[] Source, byte[] Target, byte[] Delta)> Fixture = new(() =>
		(File.ReadAllBytes(PatchPath("a.test")), File.ReadAllBytes(PatchPath("b.test")), File.ReadAllBytes(PatchPath(LZMA_PATCH))));

	private static byte[] DecodeLegacy(byte[] source, byte[] delta, int trickleSeed)
	{
		using var src = new MemoryStream(source, false);
		using var deltaStream = trickleSeed < 0 ? (Stream)new MemoryStream(delta, false) : new TrickleStream(new MemoryStream(delta, false), trickleSeed);
		using var output = new MemoryStream();
		using var decoder = new VcDecoder(src, deltaStream, output);
		Assert.Equal(VcDiffResult.SUCCESS, decoder.Decode(out _));
		return output.ToArray();
	}

	private static byte[] DecodeStreaming(byte[] source, byte[] delta, int inChunk, int outChunk)
	{
		using var dec = new VcDiffDecoder(new ReadOnlySequence<byte>(source));
		var result = new MemoryStream();
		var outBuf = new byte[outChunk];
		var pos = 0;
		while (pos < delta.Length)
		{
			var take = Math.Min(inChunk, delta.Length - pos);
			var input = delta.AsSpan(pos, take);
			pos += take;

			OperationStatus status;
			do
			{
				status = dec.Decode(input, outBuf, out var ic, out var ow, false);
				result.Write(outBuf, 0, ow);
				input = input.Slice(ic);
			} while (status == OperationStatus.DestinationTooSmall);

			Assert.NotEqual(OperationStatus.InvalidData, status);
		}

		while (true)
		{
			var status = dec.Decode(ReadOnlySpan<byte>.Empty, outBuf, out _, out var ow, true);
			result.Write(outBuf, 0, ow);
			Assert.NotEqual(OperationStatus.InvalidData, status);
			Assert.NotEqual(OperationStatus.NeedMoreData, status);
			if (status == OperationStatus.Done) break;
		}

		return result.ToArray();
	}

	[Theory, InlineData(1), InlineData(2), InlineData(3)]
	public void LzmaPatch_TrickleDeltaStream_Decodes(int seed)
	{
		var (source, target, delta) = Fixture.Value;
		Assert.True(target.AsSpan().SequenceEqual(DecodeLegacy(source, delta, seed)));
	}

	[Fact]
	public void LzmaPatch_DecodedTwiceInARow()
	{
		var (source, target, delta) = Fixture.Value;
		Assert.True(target.AsSpan().SequenceEqual(DecodeLegacy(source, delta, -1)));
		Assert.True(target.AsSpan().SequenceEqual(DecodeLegacy(source, delta, -1)));
		Assert.True(target.AsSpan().SequenceEqual(DecodeStreaming(source, delta, 3, 4096)));
		Assert.True(target.AsSpan().SequenceEqual(DecodeStreaming(source, delta, 1, 777)));
	}

	[Fact]
	public void LzmaPatch_DecodedInParallel()
	{
		var (source, target, delta) = Fixture.Value;
		var failures = 0;
		Parallel.For(0, 8, i =>
		{
			var result = i % 2 == 0 ? DecodeLegacy(source, delta, i % 4 == 0 ? -1 : i) : DecodeStreaming(source, delta, 1 + i * 997, 4096);
			if (!target.AsSpan().SequenceEqual(result)) System.Threading.Interlocked.Increment(ref failures);
		});
		Assert.Equal(0, failures);
	}

	// ------------------------------------------------------------------ XzSectionDecompressor over multi-segment sequences

	private static int ReadVarInt(byte[] data, ref int pos)
	{
		var result = 0;
		while (true)
		{
			var b = data[pos++];
			result = (result << 7) | (b & 0x7F);
			if ((b & 0x80) == 0) return result;
		}
	}

	/// <summary>Minimal xdelta3 window walker: yields every secondary-compressed section in file order.</summary>
	private static List<(WindowSectionType Type, byte[] Data)> ExtractCompressedSections(byte[] delta)
	{
		var sections = new List<(WindowSectionType, byte[])>();
		var pos = 4;
		var hdrIndicator = delta[pos++];
		Assert.True((hdrIndicator & 0x01) != 0); // VCD_DECOMPRESS
		Assert.Equal(2, delta[pos++]); // xz/lzma
		Assert.True((hdrIndicator & 0x02) == 0); // no custom code table in this fixture
		if ((hdrIndicator & 0x04) != 0)
		{
			var appLen = ReadVarInt(delta, ref pos);
			pos += appLen;
		}

		while (pos < delta.Length)
		{
			var winIndicator = delta[pos++];
			if ((winIndicator & 0x03) != 0)
			{
				ReadVarInt(delta, ref pos);
				ReadVarInt(delta, ref pos);
			}

			ReadVarInt(delta, ref pos); // delta encoding length
			ReadVarInt(delta, ref pos); // target window length
			var deltaIndicator = delta[pos++];
			var addRunLen = ReadVarInt(delta, ref pos);
			var instLen = ReadVarInt(delta, ref pos);
			var addrLen = ReadVarInt(delta, ref pos);
			if ((winIndicator & 0x04) != 0) pos += 4; // adler32

			void Take(int len, int flag, WindowSectionType type, ref int p)
			{
				if ((deltaIndicator & flag) != 0) sections.Add((type, delta.AsSpan(p, len).ToArray()));
				p += len;
			}

			Take(addRunLen, 0x01, WindowSectionType.AddRunData, ref pos);
			Take(instLen, 0x02, WindowSectionType.InstructionsAndSizes, ref pos);
			Take(addrLen, 0x04, WindowSectionType.AddressForCopy, ref pos);
		}

		Assert.Equal(delta.Length, pos);
		return sections;
	}

	private static byte[] Decompress(XzSectionDecompressor decompressor, WindowSectionType type, ReadOnlySequence<byte> data)
	{
		using var rental = decompressor.Decompress(type, data);
		return rental.AsSpan().ToArray();
	}

	[Theory, InlineData(1), InlineData(2), InlineData(3), InlineData(17), InlineData(4096)]
	public void XzSectionDecompressor_MultiSegmentInput_MatchesContiguous(int maxSegment)
	{
		var sections = ExtractCompressedSections(Fixture.Value.Delta);
		Assert.NotEmpty(sections);

		using var contiguous = new XzSectionDecompressor();
		using var segmented = new XzSectionDecompressor();
		var rnd = new Random(maxSegment);
		foreach (var (type, data) in sections)
		{
			var expected = Decompress(contiguous, type, new ReadOnlySequence<byte>(data));
			var copy = (byte[])data.Clone();
			var actual = Decompress(segmented, type, MultiSegment(copy, RandomSizes(rnd, maxSegment).Select(s => Math.Max(1, s))));
			Assert.True(expected.AsSpan().SequenceEqual(actual));

			// The decompressor must not read caller memory after Decompress returns.
			Array.Clear(copy, 0, copy.Length);
		}
	}

	[Fact]
	public void XzSectionDecompressor_InvalidLength_DoesNotLeakCallerMemory()
	{
		using var decompressor = new XzSectionDecompressor();

		// Varint overflow => negative (error) length.
		var bogus = new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x7F, 0x00 };
		Assert.ThrowsAny<Exception>(() => Decompress(decompressor, WindowSectionType.AddRunData, new ReadOnlySequence<byte>(bogus)));

		var field = typeof(XzSectionDecompressor).GetField("addRunCompressedBuffer", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
		var stream = (Stream)field.GetValue(decompressor)!;
		Assert.Equal(0, stream.Length);
	}

	/// <summary>
	///     Truncated secondary-compressed input must surface as <see cref="IncompleteArchiveException" /> on every target
	///     framework (on net8.0 a call bound to the BCL <c>Stream.ReadExactly</c> threw EndOfStreamException instead).
	/// </summary>
	[Fact]
	public void XzSectionDecompressor_TruncatedSection_ThrowsIncompleteArchive()
	{
		var (type, data) = ExtractCompressedSections(Fixture.Value.Delta)[0];
		var prefixLength = 0;
		ReadVarInt(data, ref prefixLength); // big-endian uncompressed-length varint in front of the xz data

		var step = Math.Max(1, (data.Length - prefixLength) / 200);
		for (var cut = prefixLength; cut < data.Length; cut += step)
		{
			using var decompressor = new XzSectionDecompressor();
			var truncated = new ReadOnlySequence<byte>(data, 0, cut);
			Assert.Throws<IncompleteArchiveException>(() => Decompress(decompressor, type, truncated));
		}
	}

	[Fact]
	public void XzStream_TruncatedHeader_ThrowsIncompleteArchive()
	{
		using var xz = new VCDiff.Compression.Xz.XzStream(new MemoryStream(new byte[] { 0xFD, 0x37, 0x7A }));
		Assert.Throws<IncompleteArchiveException>(() => xz.ReadExactOrThrow(new byte[1]));
		Assert.Throws<IncompleteArchiveException>(() => new MemoryStream(new byte[3]).ReadExactOrThrow(new byte[4]));
	}
}
