using BenchmarkDotNet.Running;

namespace VCDiff.Benchmark;

internal class Program
{
	private static void Main(string[] args)
	{
		// Discovers every [Benchmark] class in this assembly. With no arguments it runs them all;
		// pass BenchmarkDotNet switches to narrow the run, e.g.:
		//   dotnet run -c Release -- --filter "*SpanEncode*"
		var switcher = BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly);
		if (args.Length == 0)
			switcher.RunAll();
		else
			switcher.Run(args);
	}
}
