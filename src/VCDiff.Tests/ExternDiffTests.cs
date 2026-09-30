using System.IO;
using System.Security.Cryptography;
using System.Threading.Tasks;
using VCDiff.Decoders;
using VCDiff.Includes;
using Xunit;

namespace VCDiff.Tests;

public class ExternDiffTests
{
	[Theory, InlineData("checksum.openvcdiff"), InlineData("checksum_interleaved.openvcdiff"), InlineData("patch.openvcdiff"), InlineData("interleaved.openvcdiff"),
	InlineData("sample.xdelta"), InlineData("sample_nosmallstr.xdelta"), InlineData("sample_appheader.xdelta"), InlineData("a-to-b-lzma-compression.xdelta")]
	public async Task ExternTest_ImplAsync(string patchfile)
	{
		using var srcStream = File.OpenRead($"patches{Path.DirectorySeparatorChar}a.test");
		using var targetStream = File.OpenRead($"patches{Path.DirectorySeparatorChar}b.test");
		using var deltaStream = File.OpenRead($"patches{Path.DirectorySeparatorChar}{patchfile}");
		using var md5 = MD5.Create();
		targetStream.Position = 0;
		var originalHash = md5.ComputeHash(targetStream);

		using var outputStream = new MemoryStream();

		outputStream.Position = 0;
		srcStream.Position = 0;
		targetStream.Position = 0;
		deltaStream.Position = 0;

		using var decoder = new VcDecoder(srcStream, deltaStream, outputStream);
		var (result, _) = await decoder.DecodeAsync();
		Assert.Equal(VcDiffResult.SUCCESS, result);
		outputStream.Position = 0;
		var outputHash = md5.ComputeHash(outputStream);
		Assert.Equal(originalHash, outputHash);
	}

	[Theory, InlineData("checksum.openvcdiff"), InlineData("checksum_interleaved.openvcdiff"), InlineData("patch.openvcdiff"), InlineData("interleaved.openvcdiff"),
	InlineData("sample.xdelta"), InlineData("sample_nosmallstr.xdelta"), InlineData("sample_appheader.xdelta"), InlineData("a-to-b-lzma-compression.xdelta")]
	public void ExternTest_Impl(string patchfile)
	{
		using var srcStream = File.OpenRead($"patches{Path.DirectorySeparatorChar}a.test");
		using var targetStream = File.OpenRead($"patches{Path.DirectorySeparatorChar}b.test");
		using var deltaStream = File.OpenRead($"patches{Path.DirectorySeparatorChar}{patchfile}");
		using var md5 = MD5.Create();
		targetStream.Position = 0;
		var originalHash = md5.ComputeHash(targetStream);

		using var outputStream = new MemoryStream();

		outputStream.Position = 0;
		srcStream.Position = 0;
		targetStream.Position = 0;
		deltaStream.Position = 0;

		using var decoder = new VcDecoder(srcStream, deltaStream, outputStream);
		Assert.Equal(VcDiffResult.SUCCESS, decoder.Decode(out var bytesWritten));
		outputStream.Position = 0;
		var outputHash = md5.ComputeHash(outputStream);
		Assert.Equal(originalHash, outputHash);
	}
}