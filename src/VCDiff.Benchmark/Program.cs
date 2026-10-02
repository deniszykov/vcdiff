using BenchmarkDotNet.Running;

namespace VCDiff.Benchmark;

internal class Program
{
	private static void Main(string[] args)
	{
		// Discovers every [Benchmark] class in this assembly. Forward args so BenchmarkDotNet's
		// switches work, e.g.: dotnet run -c Release -- --filter "*SpanEncode*" --job short
		BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
	}
}
