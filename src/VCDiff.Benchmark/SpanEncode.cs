using System;
using System.Buffers;
using BenchmarkDotNet.Attributes;
using VCDiff.Encoders;

namespace VCDiff.Benchmark;

/// <summary>
///     Benchmarks the streaming <see cref="VcdiffSpanEncoder" />, including the per-window source segment
///     (<see cref="VcdiffSpanEncoder.SetSourceSegment" />) added for bounded encoder memory. The returned value is the
///     encoded delta length, so compression ratio is <c>delta / target</c>.
/// </summary>
[MemoryDiagnoser, SimpleJob(1, 3, 8)]
public class SpanEncode
{
	private const int WINDOW_SIZE = 1024 * 1024;

	private byte[] _dict;
	private byte[] _heavy;
	private byte[] _slight;
	private ReadOnlySequence<byte> _dictionary;
	private byte[] _outBuf;

	[Params(16 * 1024 * 1024)] // 16 MiB
	public int Bytes { get; set; }

	[Params(16, 32)]
	public int BlockSize { get; set; }

	[GlobalSetup]
	public void GlobalSetup()
	{
		this._dict = RandomDataGenerator.GetRandomBytes(this.Bytes);
		RandomDataEncode.MakeRandomData(this._dict, out this._slight, out this._heavy);
		this._dictionary = new ReadOnlySequence<byte>(this._dict);

		// Large enough that one window's delta always fits in a single drain.
		this._outBuf = new byte[WINDOW_SIZE + 64 * 1024];
	}

	[Benchmark]
	public long SlightFullDictionary() => this.Encode(this._slight, restrict: false);

	[Benchmark]
	public long SlightRestrictedSegment() => this.Encode(this._slight, restrict: true);

	[Benchmark]
	public long HeavyFullDictionary() => this.Encode(this._heavy, restrict: false);

	[Benchmark]
	public long HeavyRestrictedSegment() => this.Encode(this._heavy, restrict: true);

	private long Encode(byte[] target, bool restrict)
	{
		using var encoder = new VcdiffSpanEncoder(this._dictionary, new VcdiffEncoderOptions { BlockSize = this.BlockSize });
		if (restrict)
			encoder.SetSourceSegment(0, this.Bytes / 2);

		long written = 0;
		var input = target.AsSpan();
		while (input.Length > 0)
		{
			var status = encoder.Encode(input, this._outBuf, out var consumed, out var produced, isFinal: false);
			written += produced;
			input = input.Slice(consumed);
			if (status == OperationStatus.DestinationTooSmall) continue;
			if (status == OperationStatus.NeedMoreData) break;
			// Done: a window was emitted; continue with the remaining input.
		}

		while (true)
		{
			var status = encoder.Encode(ReadOnlySpan<byte>.Empty, this._outBuf, out _, out var produced, isFinal: true);
			written += produced;
			if (status == OperationStatus.Done) break;
		}

		return written;
	}
}
