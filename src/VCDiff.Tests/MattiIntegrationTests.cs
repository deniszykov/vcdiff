using System;
using System.IO;
using VCDiff.Decoders;
using VCDiff.Encoders;
using VCDiff.Includes;
using Xunit;

namespace VCDiff.Tests;

public class MattiIntegrationTests
{
	private static readonly Random Random = new(DateTime.Now.GetHashCode());

	private byte[] CreateRandomByteArray(int size)
	{
		var buffer = new byte[size];

		Random.NextBytes(buffer);

		return buffer;
	}

	private void AddRandomPiecesIn(byte[] input)
	{
		var size = 1024 * 100; // 100 KB

		for (var i = 0; i < 100; i++)
		{
			var difference = this.CreateRandomByteArray(size);

			var index = Random.Next(0, input.Length - size - 1);

			for (var x = 0; x < size; x++) input[x + index] = difference[x];
		}
	}

	[Fact]
	public void TestEncodeAndDecodeShouldBeTheSame()
	{
		var size = 20 * 1024 * 1024; // 20 MB

		var oldData = this.CreateRandomByteArray(size);
		var newData = new byte[size];

		oldData.CopyTo(newData, 0);

		this.AddRandomPiecesIn(oldData);

		var sOld = new MemoryStream(oldData);
		var sNew = new MemoryStream(newData);
		var sDelta = new MemoryStream(new byte[size], true);

		var coder = new VcdiffEncoder(sOld, sNew, sDelta);
		Assert.Equal(VcdiffResult.Success, coder.Encode());

		sDelta.SetLength(sDelta.Position);
		sDelta.Position = 0;
		sOld.Position = 0;
		sNew.Position = 0;

		var sPatched = new MemoryStream(new byte[size], true);

		var decoder = new VcdiffDecoder(sOld, sDelta, sPatched);
		Assert.Equal(VcdiffResult.Success, decoder.Decode(out var bytesWritten));

		Assert.Equal(sNew.ToArray(), sPatched.ToArray());
	}
}