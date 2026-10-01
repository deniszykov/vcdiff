using System;
using System.Buffers;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.IO;
using VCDiff.Decoders;
using VCDiff.Encoders;
using VCDiff.Shared;
using Xunit;

namespace VCDiff.Tests;

// Regression tests for bugs found in the library-wide review.
public class LibraryFixTests
{
	// ------------------------------------------------------------------ compound opcodes

	/// <summary>
	///     The second-opcode map of <see cref="InstructionMap" /> was filed under the compound opcode instead of the
	///     first opcode, so the encoder never emitted the compound (two instruction) opcodes of the default code table.
	/// </summary>
	[Fact]
	public void WindowEncoder_EmitsCompoundOpcodes()
	{
		var dictionary = new byte[64];
		new Random(5).NextBytes(dictionary);

		var manager = new RecyclableMemoryStreamManager();
		using var encoder = new WindowEncoder(dictionary.Length, ChecksumFormat.None, false, manager);
		encoder.Reset(0);
		encoder.Add(new byte[] { 0xAA }); // ADD 1
		encoder.Copy(10, 4); // COPY 4 SELF => ADD 1 + COPY 4 mode 0 (opcode 163)
		encoder.Copy(20, 4); // COPY 4 NEAR0 (+10)
		encoder.Add(new byte[] { 0xBB }); // ADD 1 => COPY 4 mode 2 + ADD 1 (opcode 249)

		var window = new MemoryStream();
		encoder.Output(window);
		var bytes = window.ToArray();

		// [VCD_SOURCE][64][0][delta length][target 10][indicator][add/run 2][instructions][addresses]...
		Assert.Equal(10, bytes[4]);
		Assert.Equal(2, bytes[6]);
		Assert.Equal(2, bytes[7]);
		Assert.Equal(new byte[] { 163, 249 }, bytes.AsSpan(9 + 2, 2).ToArray());

		var delta = new MemoryStream();
		delta.Write(FileHeader.Get(false).Span);
		delta.Write(bytes);

		var expected = new byte[10];
		expected[0] = 0xAA;
		dictionary.AsSpan(10, 4).CopyTo(expected.AsSpan(1));
		dictionary.AsSpan(20, 4).CopyTo(expected.AsSpan(5));
		expected[9] = 0xBB;

		using var decoder = new VcDiffDecoder(new ReadOnlySequence<byte>(dictionary));
		var output = new byte[16];
		var status = decoder.Decode(delta.ToArray(), output, out _, out var written, true);
		Assert.Equal(OperationStatus.Done, status);
		Assert.Equal(expected, output.AsSpan(0, written).ToArray());
	}

	[Fact]
	public void SmallBlockSize_RoundTripsWithCompoundOpcodes()
	{
		var dictionary = new byte[4096];
		new Random(9).NextBytes(dictionary);

		// Short dictionary matches separated by single new bytes: ADD 1 / COPY 4..6 pairs.
		var rnd = new Random(10);
		var target = new MemoryStream();
		while (target.Length < 3000)
		{
			target.WriteByte((byte)rnd.Next(256));
			target.Write(dictionary, rnd.Next(dictionary.Length - 8), 4 + rnd.Next(3));
		}

		var options = new VcEncoderOptions { BlockSize = 2, ChunkSize = 4 };
		var delta = Encode(dictionary, target.ToArray(), options);
		Assert.Equal(target.ToArray(), Decode(dictionary, delta));
	}

	// ------------------------------------------------------------------ rolling hash over-read

	[DllImport("kernel32", SetLastError = true)]
	private static extern IntPtr VirtualAlloc(IntPtr address, UIntPtr size, uint allocationType, uint protect);

	[DllImport("kernel32", SetLastError = true)]
	private static extern bool VirtualProtect(IntPtr address, UIntPtr size, uint newProtect, out uint oldProtect);

	[DllImport("kernel32", SetLastError = true)]
	private static extern bool VirtualFree(IntPtr address, UIntPtr size, uint freeType);

	/// <summary>
	///     The SIMD rolling hash loaded 16 bytes to hash 8 (AVX2) or 4 (SSE4.1), reading up to 12 bytes past the block.
	///     A target that ends right before an inaccessible page crashed the process.
	/// </summary>
	[Theory, InlineData(16), InlineData(20), InlineData(32)]
	public unsafe void Encoder_DoesNotReadPastTheEndOfTheTarget(int blockSize)
	{
		if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
			return;

		const int page = 4096;
		var memory = VirtualAlloc(IntPtr.Zero, (UIntPtr)(2 * page), 0x3000 /* MEM_COMMIT | MEM_RESERVE */, 0x04 /* PAGE_READWRITE */);
		Assert.NotEqual(IntPtr.Zero, memory);
		try
		{
			Assert.True(VirtualProtect(memory + page, (UIntPtr)page, 0x01 /* PAGE_NOACCESS */, out _));

			var dictionary = new byte[1024];
			new Random(3).NextBytes(dictionary);

			// Targets whose last block is hashed in full, ending exactly at the guard page.
			foreach (var length in new[] { blockSize, blockSize + 1, 2 * blockSize + 3, 3 * blockSize })
			{
				var target = new Span<byte>((byte*)memory + page - length, length);
				dictionary.AsSpan(100, length).CopyTo(target);
				if (length > blockSize)
					target[0] ^= 0xFF;

				using var encoder = new VcDiffEncoder(new ReadOnlySequence<byte>(dictionary), new VcEncoderOptions { BlockSize = blockSize });
				var output = new byte[4096];
				Assert.Equal(OperationStatus.Done, encoder.Encode(target, output, out var consumed, out var written, true));
				Assert.Equal(length, consumed);
				Assert.Equal(target.ToArray(), Decode(dictionary, output.AsSpan(0, written).ToArray()));
			}
		}
		finally
		{
			VirtualFree(memory, UIntPtr.Zero, 0x8000 /* MEM_RELEASE */);
		}
	}

	// ------------------------------------------------------------------ header length overflow

	/// <summary>
	///     A header section length near <see cref="int.MaxValue" /> overflowed the "is it all buffered" check, so the
	///     decoder skipped a negative length and threw <see cref="ArgumentOutOfRangeException" /> instead of waiting
	///     for (or reporting the lack of) the rest of the header.
	/// </summary>
	[Theory]
	[InlineData((byte)0x04)] // VCD_APPHEADER
	[InlineData((byte)0x02)] // VCD_CODETABLE
	public void HugeHeaderSectionLength_IsTruncationNotException(byte indicator)
	{
		var delta = new byte[] { 0xD6, 0xC3, 0xC4, 0x00, indicator, 0x87, 0xFF, 0xFF, 0xFF, 0x7F, 1, 2, 3 };
		using var decoder = new VcDiffDecoder(new ReadOnlySequence<byte>(new byte[16]));
		var output = new byte[16];

		Assert.Equal(OperationStatus.NeedMoreData, decoder.Decode(delta, output, out var consumed, out _, false));
		Assert.Equal(delta.Length, consumed);
		Assert.Equal(OperationStatus.InvalidData, decoder.Decode(ReadOnlySpan<byte>.Empty, output, out _, out _, true));
	}

	// ------------------------------------------------------------------ Adler-32

	[Fact]
	public void Adler32_OfNoData_KeepsTheRunningValue()
	{
		Assert.Equal(0x12345678u, Adler32.Hash(0x12345678u, ReadOnlySpan<byte>.Empty));
		Assert.Equal(1u, Adler32.Hash(1u, ReadOnlySpan<byte>.Empty));
	}

	// ------------------------------------------------------------------ helpers

	private static byte[] Encode(byte[] dictionary, byte[] target, VcEncoderOptions options)
	{
		using var encoder = new VcDiffEncoder(new ReadOnlySequence<byte>(dictionary), options);
		var delta = new MemoryStream();
		var output = new byte[1024];
		var input = target.AsSpan();
		OperationStatus status;
		do
		{
			status = encoder.Encode(input, output, out var consumed, out var written, true);
			delta.Write(output, 0, written);
			input = input.Slice(consumed);
		} while (status != OperationStatus.Done || input.Length > 0);

		return delta.ToArray();
	}

	private static byte[] Decode(byte[] dictionary, byte[] delta)
	{
		using var decoder = new VcDiffDecoder(new ReadOnlySequence<byte>(dictionary));
		var target = new MemoryStream();
		var output = new byte[1024];
		var input = delta.AsSpan();
		OperationStatus status;
		do
		{
			status = decoder.Decode(input, output, out var consumed, out var written, true);
			target.Write(output, 0, written);
			input = input.Slice(consumed);
		} while (status == OperationStatus.DestinationTooSmall);

		Assert.Equal(OperationStatus.Done, status);
		return target.ToArray();
	}
}
