using System.IO;
using BenchmarkDotNet.Attributes;
using VCDiff.Decoders;
using VCDiff.Encoders;

namespace VCDiff.Benchmark;

[MemoryDiagnoser, SimpleJob(1, 3, 8)]
public class RandomDataDecode
{
	private const int REPEAT_COUNT = 128;
	private byte[] _data;
	private Stream _patchHeavyModified;
	private Stream _patchSlightModified;

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
		RandomDataEncode.MakeRandomData(this._data, out var dataSlightModified, out var dataHeavyModified);

		// Make Source Stream
		this._sourceStream = new MemoryStream(this._data);

		// Make Slightly Mod Patch
		this.CreatePatch(dataSlightModified, ref this._patchSlightModified);
		this.CreatePatch(dataHeavyModified, ref this._patchHeavyModified);
	}

	private void CreatePatch(byte[] targetData, ref Stream receiver)
	{
		this._sourceStream.Seek(0, SeekOrigin.Begin);
		receiver = new MemoryStream(this._data.Length);
		using var targetStream = new MemoryStream(targetData);
		using var encoder = new VcEncoder(this._sourceStream, targetStream, receiver, 1, this.BlockSize);
		encoder.Encode();
	}

	[Benchmark]
	public void DecodeSlightlyModified()
	{
		using var result = new MemoryStream((int)this._sourceStream.Length);
		for (var x = 0; x < REPEAT_COUNT; x++)
		{
			this._sourceStream.Seek(0, SeekOrigin.Begin);
			this._patchSlightModified.Seek(0, SeekOrigin.Begin);
			result.Position = 0;
			using var decoder = new VcDecoder(this._sourceStream, this._patchSlightModified, result, int.MaxValue);
			decoder.Decode(out var written);
		}
	}

	[Benchmark]
	public void DecodeHeavilyModified()
	{
		using var result = new MemoryStream((int)this._sourceStream.Length);
		for (var x = 0; x < REPEAT_COUNT; x++)
		{
			this._sourceStream.Seek(0, SeekOrigin.Begin);
			this._patchHeavyModified.Seek(0, SeekOrigin.Begin);
			result.Position = 0;
			using var decoder = new VcDecoder(this._sourceStream, this._patchHeavyModified, result, int.MaxValue);
			decoder.Decode(out var written);
		}
	}
}