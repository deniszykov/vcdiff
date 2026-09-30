using System.IO;
using System.Security.Cryptography;
using VCDiff.Decoders;
using VCDiff.Includes;
using Xunit;

namespace VCDiff.Tests;

public class WindowDecoderTests
{
	[Fact]
	public void WinIndicator_Zero()
	{
		var inputStream = File.OpenRead($"patches{Path.DirectorySeparatorChar}empty.test");
		var targetStream = File.OpenRead($"patches{Path.DirectorySeparatorChar}win_indicator_zero.test");
		var deltaStream = File.OpenRead($"patches{Path.DirectorySeparatorChar}win_indicator_zero.xdelta");
		using var md5 = MD5.Create();
		targetStream.Position = 0;
		var originalHash = md5.ComputeHash(targetStream);

		using var outputStream = new MemoryStream();

		var decoder = new VcDecoder(inputStream, deltaStream, outputStream);
		Assert.Equal(VcDiffResult.SUCCESS, decoder.Decode(out var bytesWritten));
		outputStream.Position = 0;
		var outputHash = md5.ComputeHash(outputStream);
		Assert.Equal(originalHash, outputHash);
	}
}