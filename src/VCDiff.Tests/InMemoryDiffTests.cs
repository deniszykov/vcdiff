using System;
using System.IO;
using System.Text;
using VCDiff.Decoders;
using VCDiff.Encoders;
using VCDiff.Includes;
using VCDiff.Shared;
using Xunit;

namespace VCDiff.Tests;

public class InMemoryDiffTests
{
	private static readonly ReadOnlyMemory<byte> ADiffData = Encoding.UTF8.GetBytes("Hello World");
	private static readonly ReadOnlyMemory<byte> BDiffData = Encoding.UTF8.GetBytes("Goodbye World");

	[Fact]
	public void Checksum_Test()
	{
		using var srcStream = new MemoryStream(ADiffData.ToArray());
		using var targetStream = new MemoryStream(BDiffData.ToArray());
		using var deltaStream = new MemoryStream();
		using var outputStream = new MemoryStream();
		var coder = new VcdiffEncoder(srcStream, targetStream, deltaStream);
		var result = coder.Encode(checksumFormat: WindowChecksumFormat.Sdch); //encodes with no checksum and not interleaved
		Assert.Equal(VcdiffResult.Success, result);

		srcStream.Position = 0;
		targetStream.Position = 0;
		deltaStream.Position = 0;

		var decoder = new VcdiffDecoder(srcStream, deltaStream, outputStream);
		Assert.Equal(VcdiffResult.Success, decoder.Decode(out var bytesWritten));

		Assert.Equal("Goodbye World", Encoding.UTF8.GetString(outputStream.ToArray()));
	}

	[Fact]
	public void Interleaved_Test()
	{
		using var srcStream = new MemoryStream(ADiffData.ToArray());
		using var targetStream = new MemoryStream(BDiffData.ToArray());
		using var deltaStream = new MemoryStream();
		using var outputStream = new MemoryStream();
		var coder = new VcdiffEncoder(srcStream, targetStream, deltaStream);
		var result = coder.Encode(true); //encodes with no checksum and not interleaved
		Assert.Equal(VcdiffResult.Success, result);

		srcStream.Position = 0;
		targetStream.Position = 0;
		deltaStream.Position = 0;

		var decoder = new VcdiffDecoder(srcStream, deltaStream, outputStream);

		long bytesWritten = 0;

		while (bytesWritten < BDiffData.Length)
		{
			Assert.Equal(VcdiffResult.Success, decoder.Decode(out var chunk));
			bytesWritten += chunk;
		}

		Assert.Equal("Goodbye World", Encoding.UTF8.GetString(outputStream.ToArray()));
	}

	[Fact]
	public void NoChecksumNoInterleaved_Test()
	{
		using var srcStream = new MemoryStream(ADiffData.ToArray());
		using var targetStream = new MemoryStream(BDiffData.ToArray());
		using var deltaStream = new MemoryStream();
		using var outputStream = new MemoryStream();
		var coder = new VcdiffEncoder(srcStream, targetStream, deltaStream);
		var result = coder.Encode(); //encodes with no checksum and not interleaved
		Assert.Equal(VcdiffResult.Success, result);

		srcStream.Position = 0;
		targetStream.Position = 0;
		deltaStream.Position = 0;

		var decoder = new VcdiffDecoder(srcStream, deltaStream, outputStream);
		Assert.Equal(VcdiffResult.Success, decoder.Decode(out var bytesWritten));

		Assert.Equal("Goodbye World", Encoding.UTF8.GetString(outputStream.ToArray()));
		Assert.NotEqual(0, bytesWritten);
	}
}