// Copyright (c) Denis Zykov and contributors (DeepSeek, ClaudeCode).
// Licensed under the Apache License, Version 2.0.

using System;
using System.Buffers.Binary;
using System.IO;

namespace VCDiff.Compression;

/// <summary>
///     Read loops and integer parsing shared by the XZ / LZMA decoders. Every helper reports truncated input as
///     <see cref="VcdiffException" /> on all target frameworks (deliberately not named like the BCL
///     <c>Stream.ReadExactly</c>, which throws <see cref="EndOfStreamException" /> and would win overload resolution).
/// </summary>
internal static class ReadHelpers
{
	/// <summary>Largest XZ variable-length integer (7 bits per byte, 63-bit value).</summary>
	private const int MAX_XZ_INTEGER_LENGTH = 9;

	/// <summary>
	///     Reads until <paramref name="buffer" /> is full, tolerating short reads from <paramref name="stream" />.
	/// </summary>
	/// <exception cref="VcdiffException">The stream ended before the buffer was filled.</exception>
	public static void ReadExactOrThrow(this Stream stream, Span<byte> buffer)
	{
		var total = 0;
		while (total < buffer.Length)
		{
			var read = stream.Read(buffer.Slice(total));
			if (read <= 0) throw VcdiffException.UnexpectedEndOfStream();

			total += read;
		}
	}

	/// <exception cref="VcdiffException">The stream is at its end.</exception>
	public static byte ReadByteOrThrow(this Stream stream)
	{
		var value = stream.ReadByte();
		if (value < 0) throw VcdiffException.UnexpectedEndOfStream();

		return (byte)value;
	}

	/// <exception cref="VcdiffException">The stream ended before 4 bytes were read.</exception>
	public static uint ReadUInt32LittleEndianOrThrow(this Stream stream)
	{
		Span<byte> bytes = stackalloc byte[sizeof(uint)];
		stream.ReadExactOrThrow(bytes);
		return BinaryPrimitives.ReadUInt32LittleEndian(bytes);
	}

	/// <summary>
	///     Parses an XZ variable-length integer (little-endian base-128, at most 9 bytes, no trailing zero byte) from
	///     the start of <paramref name="buffer" />.
	/// </summary>
	/// <exception cref="VcdiffException">The integer is malformed or runs past the end of the buffer.</exception>
	public static ulong ParseXzInteger(ReadOnlySpan<byte> buffer, out int bytesConsumed)
	{
		if (buffer.IsEmpty) throw VcdiffException.TruncatedXzInteger();

		var lastByte = buffer[0];
		var output = (ulong)lastByte & 0x7F;

		var i = 0;
		while ((lastByte & 0x80) != 0)
		{
			if (++i >= MAX_XZ_INTEGER_LENGTH) throw VcdiffException.XzIntegerTooLong();
			if (i >= buffer.Length) throw VcdiffException.TruncatedXzInteger();

			lastByte = buffer[i];
			if (lastByte == 0) throw VcdiffException.NonCanonicalXzInteger();

			output |= (ulong)(lastByte & 0x7F) << (i * 7);
		}

		bytesConsumed = i + 1;
		return output;
	}

	/// <summary>
	///     Reads an XZ variable-length integer byte by byte from <paramref name="stream" />, appending the raw bytes to
	///     <paramref name="crc32" /> (seeded state, see <c>Crc32.Update</c>).
	/// </summary>
	/// <exception cref="VcdiffException">The stream ended inside the integer.</exception>
	/// <exception cref="VcdiffException">The integer is malformed.</exception>
	public static ulong ReadXzInteger(this Stream stream, ref uint crc32, out int bytesConsumed)
	{
		Span<byte> bytes = stackalloc byte[MAX_XZ_INTEGER_LENGTH];
		var length = 0;
		do
		{
			if (length == MAX_XZ_INTEGER_LENGTH) throw VcdiffException.XzIntegerTooLong();

			bytes[length] = stream.ReadByteOrThrow();
		} while ((bytes[length++] & 0x80) != 0);

		var raw = bytes.Slice(0, length);
		crc32 = Xz.Crc32.Update(crc32, raw);
		return ParseXzInteger(raw, out bytesConsumed);
	}

	public static bool IsAllZero(ReadOnlySpan<byte> bytes)
	{
		foreach (var b in bytes)
			if (b != 0)
				return false;

		return true;
	}
}
