using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using VCDiff.Decoders;
using VCDiff.Encoders;
using VCDiff.Includes;
using VCDiff.Shared;
using Xunit;
using Xunit.Abstractions;

namespace VCDiff.Tests;

/// <summary>
///     Interop tests that run the external <c>xdelta3</c> binary (https://github.com/jmacd/xdelta) against the deltas
///     produced by this encoder. They are skipped when <c>xdelta3</c> is not on <c>PATH</c> (set <c>XDELTA3</c> to an
///     explicit path to override the search), so they run wherever xdelta3 is installed — typically CI.
/// </summary>
public class Xdelta3InteropTests
{
	private readonly ITestOutputHelper _output;

	public Xdelta3InteropTests(ITestOutputHelper output)
	{
		this._output = output;
	}

	// ------------------------------------------------------------------ helpers

	private static ReadOnlySequence<byte> Seq(byte[] data) => new(data);

	private static byte[] MakeDictionary(int seed = 1, int length = 4096)
	{
		var rnd = new Random(seed);
		var data = new byte[length];
		rnd.NextBytes(data);
		return data;
	}

	private static byte[] MakeTarget(byte[] dict, int seed = 2)
	{
		var rnd = new Random(seed);
		var target = new byte[dict.Length + 16384];
		dict.CopyTo(target, 0);
		rnd.NextBytes(target.AsSpan(dict.Length));

		for (var i = 0; i < 200; i++)
			target[rnd.Next(target.Length)] = (byte)rnd.Next(256);
		return target;
	}

	private static byte[] Encode(VcdiffSpanEncoder enc, byte[] target)
	{
		var outBuf = new byte[64 * 1024];
		var result = new MemoryStream();
		var pos = 0;
		while (pos < target.Length)
		{
			var input = target.AsSpan(pos);
			while (input.Length > 0)
			{
				var status = enc.Encode(input, outBuf, out var consumed, out var written, false);
				result.Write(outBuf, 0, written);
				input = input.Slice(consumed);
				pos += consumed;
				if (status == OperationStatus.InvalidData) throw new InvalidOperationException("encode failed");
				if (status == OperationStatus.DestinationTooSmall) continue;
				if (status == OperationStatus.NeedMoreData) break;
			}
		}

		while (true)
		{
			var status = enc.Encode(ReadOnlySpan<byte>.Empty, outBuf, out _, out var written, true);
			result.Write(outBuf, 0, written);
			if (status == OperationStatus.InvalidData) throw new InvalidOperationException("encode final failed");
			if (status == OperationStatus.Done) break;
		}

		return result.ToArray();
	}

	private static string FindXdelta3()
	{
		var explicitPath = Environment.GetEnvironmentVariable("XDELTA3");
		if (!string.IsNullOrEmpty(explicitPath) && File.Exists(explicitPath))
			return explicitPath;

		foreach (var name in OperatingSystem.IsWindows() ? new[] { "xdelta3.exe", "xdelta3" } : new[] { "xdelta3" })
		{
			var found = FindOnPath(name);
			if (found != null)
				return found;
		}

		return null;
	}

	private static string FindOnPath(string name)
	{
		var path = Environment.GetEnvironmentVariable("PATH");
		if (string.IsNullOrEmpty(path))
			return null;

		foreach (var dir in path.Split(Path.PathSeparator))
		{
			if (string.IsNullOrEmpty(dir))
				continue;

			try
			{
				var candidate = Path.Combine(dir.Trim(), name);
				if (File.Exists(candidate))
					return Path.GetFullPath(candidate);
			}
			catch (Exception)
			{
				// Ignore unreadable PATH entries.
			}
		}

		return null;
	}

	private static (int ExitCode, string StdOut, string StdErr) RunXdelta3(string xdelta3, IReadOnlyList<string> arguments)
	{
		var psi = new ProcessStartInfo {
			FileName = xdelta3,
			UseShellExecute = false,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			CreateNoWindow = true
		};
		foreach (var argument in arguments)
			psi.ArgumentList.Add(argument);

		using var process = Process.Start(psi) ?? throw new InvalidOperationException($"Failed to start {xdelta3}.");
		var stdOut = process.StandardOutput.ReadToEnd();
		var stdErr = process.StandardError.ReadToEnd();
		if (!process.WaitForExit(60_000))
		{
			try
			{
				process.Kill(entireProcessTree: true);
			}
			catch (Exception)
			{
				// The process may have exited between WaitForExit timing out and Kill.
			}

			throw new TimeoutException("xdelta3 did not exit within 60 seconds.");
		}

		return (process.ExitCode, stdOut, stdErr);
	}

	private void AssertDecodesWithXdelta3(string xdelta3, byte[] dict, byte[] delta, byte[] target)
	{
		var dir = Path.Combine(Path.GetTempPath(), "vcdiff-xdelta3-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(dir);
		try
		{
			var sourcePath = Path.Combine(dir, "source.bin");
			var deltaPath = Path.Combine(dir, "patch.vcdiff");
			var outputPath = Path.Combine(dir, "target.bin");
			File.WriteAllBytes(sourcePath, dict);
			File.WriteAllBytes(deltaPath, delta);

			var (exitCode, stdOut, stdErr) = RunXdelta3(xdelta3, new[] { "-d", "-f", "-s", sourcePath, deltaPath, outputPath });
			Assert.True(exitCode == 0, $"xdelta3 exited with code {exitCode}.\nstdout:\n{stdOut}\nstderr:\n{stdErr}");
			Assert.Equal(target, File.ReadAllBytes(outputPath));
		}
		finally
		{
			try
			{
				Directory.Delete(dir, recursive: true);
			}
			catch (Exception)
			{
				// Best effort cleanup.
			}
		}
	}

	// ------------------------------------------------------------------ tests

	[Fact]
	public void Xdelta3_Decodes_DefaultOutput()
	{
		var xdelta3 = FindXdelta3();
		if (xdelta3 == null)
		{
			this._output.WriteLine("xdelta3 not found on PATH; skipping.");
			return;
		}

		var dict = MakeDictionary();
		var target = MakeTarget(dict);

		using var enc = new VcdiffSpanEncoder(Seq(dict));
		var delta = Encode(enc, target);

		this.AssertDecodesWithXdelta3(xdelta3, dict, delta, target);
	}

	[Fact]
	public void Xdelta3_Decodes_ChecksummedOutput()
	{
		var xdelta3 = FindXdelta3();
		if (xdelta3 == null)
		{
			this._output.WriteLine("xdelta3 not found on PATH; skipping.");
			return;
		}

		var dict = MakeDictionary();
		var target = MakeTarget(dict);

		using var enc = new VcdiffSpanEncoder(Seq(dict), new VcdiffEncoderOptions { WindowChecksumFormat = WindowChecksumFormat.Xdelta3 });
		var delta = Encode(enc, target);

		this.AssertDecodesWithXdelta3(xdelta3, dict, delta, target);
	}

	[Fact]
	public void Xdelta3_Decodes_SourceSegmentOutput()
	{
		var xdelta3 = FindXdelta3();
		if (xdelta3 == null)
		{
			this._output.WriteLine("xdelta3 not found on PATH; skipping.");
			return;
		}

		var dict = MakeDictionary();
		var target = MakeTarget(dict);

		// Several segment switches (forward, overlapping, backwards) before any input is consumed.
		using var enc = new VcdiffSpanEncoder(Seq(dict));
		enc.SetSourceSegment(0, 3000);
		enc.SetSourceSegment(1500, 2500);
		enc.SetSourceSegment(500, 2000);
		var delta = Encode(enc, target);

		this.AssertDecodesWithXdelta3(xdelta3, dict, delta, target);
	}

	[Fact]
	public void Xdelta3_Decodes_ZeroLengthSegmentOutput()
	{
		var xdelta3 = FindXdelta3();
		if (xdelta3 == null)
		{
			this._output.WriteLine("xdelta3 not found on PATH; skipping.");
			return;
		}

		var dict = MakeDictionary();
		var target = MakeTarget(dict);

		// Zero-length segment means "no source": the windows omit VCD_SOURCE.
		using var enc = new VcdiffSpanEncoder(Seq(dict));
		enc.SetSourceSegment(0, 0);
		var delta = Encode(enc, target);

		this.AssertDecodesWithXdelta3(xdelta3, dict, delta, target);
	}
}
