using BenchmarkDotNet.Running;

namespace VCDiff.Benchmark;

internal class Program
{
	private static void Main(string[] args)
	{
		BenchmarkRunner.Run<RandomDataDecode>();
		BenchmarkRunner.Run<RandomDataEncode>();
	}
}