using System;
using System.Buffers;
using System.IO;
using System.IO.MemoryMappedFiles;
using VCDiff.Decoders;
using VCDiff.Encoders;
using VCDiff.Includes;
using VCDiff.Shared;
using Xunit;

namespace VCDiff.Tests;

/// <summary>
///     The memory-mapped dictionary reader must produce the same deltas and decodes as the in-memory reader, and
///     its raw-pointer members must match the mapped bytes exactly.
/// </summary>
public class MemoryMappedFileDictionaryReaderTests : IDisposable
{
	private readonly string _dir;

	public MemoryMappedFileDictionaryReaderTests()
	{
		this._dir = Path.Combine(Path.GetTempPath(), "vcdiff-mmftests-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(this._dir);
	}

	public void Dispose()
	{
		try
		{
			Directory.Delete(this._dir, true);
		}
		catch (IOException)
		{
		}
		catch (UnauthorizedAccessException)
		{
		}
	}

	private string Write(byte[] data)
	{
		var path = Path.Combine(this._dir, Guid.NewGuid().ToString("N") + ".bin");
		File.WriteAllBytes(path, data);
		return path;
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

	private static byte[] Encode(IDictionaryReader dictionary, byte[] target, VcEncoderOptions options, int inChunk = 50000, int outChunk = 4096)
	{
		using var enc = new VcDiffEncoder(dictionary, options);
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

	private static byte[] Decode(IDictionaryReader dictionary, byte[] delta, int inChunk = 50000, int outChunk = 4096)
	{
		using var dec = new VcDiffDecoder(dictionary);
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

	[Theory]
	[InlineData(false, ChecksumFormat.None, 16)]
	[InlineData(false, ChecksumFormat.Xdelta3, 32)]
	[InlineData(true, ChecksumFormat.SDCH, 32)]
	public void Encoder_Output_MatchesInMemoryDictionary(bool interleaved, ChecksumFormat checksumFormat, int blockSize)
	{
		var dict = MakeDictionary(300_000, 11);
		var target = MakeTarget(dict, 800_000, 12);
		var options = new VcEncoderOptions { Interleaved = interleaved, ChecksumFormat = checksumFormat, BlockSize = blockSize };

		var expected = Encode(new ReadOnlySequenceSource(new ReadOnlySequence<byte>(dict)), target, options);
		var actual = Encode(new MemoryMappedFileDictionaryReader(this.Write(dict)), target, options);

		Assert.Equal(expected, actual);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void Decoder_Decodes_WithMappedDictionary(bool interleaved)
	{
		var dict = MakeDictionary(300_000, 21);
		var target = MakeTarget(dict, 600_000, 22);
		var options = new VcEncoderOptions { Interleaved = interleaved, ChecksumFormat = ChecksumFormat.SDCH };
		var delta = Encode(new ReadOnlySequenceSource(new ReadOnlySequence<byte>(dict)), target, options);

		Assert.Equal(target, Decode(new MemoryMappedFileDictionaryReader(this.Write(dict)), delta));
	}

	[Fact]
	public unsafe void Reader_Methods_MatchInMemory()
	{
		var data = MakeDictionary(70_000, 31); // spans multiple pages
		using var reader = new MemoryMappedFileDictionaryReader(this.Write(data));

		Assert.Equal((long)data.Length, reader.Length);

		var copy = new byte[data.Length];
		reader.CopyTo(0, copy);
		Assert.Equal(data, copy);

		var partial = new byte[12345];
		reader.CopyTo(1000, partial);
		Assert.Equal(data.AsSpan(1000, 12345).ToArray(), partial);

		Assert.Equal(data.AsSpan(500, 4096).ToArray(), reader.Read(500, 4096).ToArray());

		fixed (byte* p = data)
		{
			Assert.True(reader.SequenceEqual(0, p, data.Length));
			Assert.True(reader.SequenceEqual(12345, p + 12345, 1000));

			Assert.Equal((long)data.Length - 7000, reader.MatchForward(7000, p + 7000, data.Length));
			Assert.Equal(7000L, reader.MatchBackward(7000, p + 7000, data.Length));
		}

		var flipped = (byte[])data.Clone();
		flipped[8000] ^= 0xFF;
		fixed (byte* q = flipped)
		{
			Assert.Equal(8000L, reader.MatchForward(0, q, data.Length));
		}
	}

	[Fact]
	public void Dispose_ReleasesTheFile()
	{
		var path = this.Write(MakeDictionary(4096, 41));
		using (var reader = new MemoryMappedFileDictionaryReader(path))
		{
			Assert.Equal(4096L, reader.Length);
		}

		File.Delete(path);
		Assert.False(File.Exists(path));
	}

	[Fact]
	public void Disposed_Throws()
	{
		var reader = new MemoryMappedFileDictionaryReader(this.Write(MakeDictionary(100, 51)));
		reader.Dispose();

		Assert.Throws<ObjectDisposedException>(() => reader.CopyTo(0, new byte[1]));
	}

	[Fact]
	public void EmptyDictionary_IsSupported()
	{
		using var reader = new MemoryMappedFileDictionaryReader(this.Write(Array.Empty<byte>()));

		Assert.Equal(0L, reader.Length);
		Assert.Equal(0, reader.Read(0, 0).Length);
	}

	[Fact]
	public void OutOfRange_Throws()
	{
		using var reader = new MemoryMappedFileDictionaryReader(this.Write(new byte[10]));

		Assert.Throws<ArgumentOutOfRangeException>(() => reader.CopyTo(9, new byte[2]));
		Assert.Throws<ArgumentOutOfRangeException>(() => reader.CopyTo(-1, new byte[1]));
		Assert.Throws<ArgumentOutOfRangeException>(() => reader.Read(1, 10));
	}

	[Fact]
	public void LeaveOpen_DoesNotDisposeTheFile()
	{
		var data = MakeDictionary(50_000, 61);
		var path = this.Write(data);
		using var file = MemoryMappedFile.CreateFromFile(path, FileMode.Open, null, data.Length, MemoryMappedFileAccess.Read);

		using (var reader = new MemoryMappedFileDictionaryReader(file, data.Length, leaveOpen: true))
		{
			var copy = new byte[data.Length];
			reader.CopyTo(0, copy);
			Assert.Equal(data, copy);
		}

		using var view = file.CreateViewAccessor(0, data.Length, MemoryMappedFileAccess.Read);
		Assert.Equal(data[0], view.ReadByte(0));
	}
}
