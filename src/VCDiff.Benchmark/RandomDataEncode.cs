using System;
using System.IO;
using BenchmarkDotNet.Attributes;
using VCDiff.Encoders;

namespace VCDiff.Benchmark;

[MemoryDiagnoser, SimpleJob(1, 3, 8)]
public class RandomDataEncode
{
	private byte[] _data;
	private byte[] _dataHeavyModified;
	private byte[] _dataSlightModified;

	private Stream _sourceStream;
	[Params( //1 * 1024 * 256,  // 0.25MiB
		//1 * 1024 * 1024, // 2 MiB
		16 * 1024 * 1024 // 16 MiB
	)]
	public int Bytes { get; set; }

	[Params(32)] // AVX Only
	//[Params(16, 32)] // AVX vs SSE
	public int BlockSize { get; set; }

	[GlobalSetup]
	public void GlobalSetup()
	{
		this._data = RandomDataGenerator.GetRandomBytes(this.Bytes);
		MakeRandomData(this._data, out this._dataSlightModified, out this._dataHeavyModified);
		this._sourceStream = new MemoryStream(this._data);
	}

	[IterationSetup]
	public void IterationSetup()
	{
		this._sourceStream.Seek(0, SeekOrigin.Begin);
	}

	[Benchmark]
	public void EncodeSlightlyModified()
	{
		using var targetStream = new MemoryStream(this._dataSlightModified);
		using var patchStream = new MemoryStream(this._data.Length);

		using var encoder = new VcEncoder(this._sourceStream, targetStream, patchStream, 1, this.BlockSize);
		encoder.Encode();
	}

	[Benchmark]
	public void EncodeHeavilyModified()
	{
		using var targetStream = new MemoryStream(this._dataHeavyModified);
		using var patchStream = new MemoryStream(this._data.Length);

		using var encoder = new VcEncoder(this._sourceStream, targetStream, patchStream, 1, this.BlockSize);
		encoder.Encode();
	}

	// Utility Methods
	public static void MakeRandomData(byte[] data, out byte[] dataSlightModified, out byte[] dataHeavyModified)
	{
		dataSlightModified = (byte[])data.Clone();
		dataHeavyModified = (byte[])data.Clone();

		var random = new Random(data.Length);
		for (var x = 0; x < dataHeavyModified.Length; x++)
		{
			var next = random.Next(0, 1000);
			if (next >= 250) // 3 / 4 chance.
				dataHeavyModified[x] += (byte)next;
			if (next >= 995) // 1 / 200 chance.
				dataSlightModified[x] += (byte)next;
		}
	}
}