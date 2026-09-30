// Copyright (c) Denis Zykov and contributors (DeepSeek, ClaudeCode).
// Licensed under the Apache License, Version 2.0.

using System;
using System.IO;

namespace VCDiff.Compression;

internal static class StreamExtensions
{
    /// <summary>
    ///     Reads into the buffer until it is full or the stream is exhausted. Returns true only
    ///     when the entire buffer was filled.
    /// </summary>
    public static bool ReadFully(this Stream stream, Span<byte> buffer)
	{
		var total = 0;
		while (total < buffer.Length)
		{
			var read = stream.Read(buffer.Slice(total));
			if (read <= 0) break;

			total += read;
		}

		return total >= buffer.Length;
	}

    /// <summary>
    ///     Reads exactly <paramref name="length" /> bytes into <paramref name="buffer" />, throwing
    ///     when the stream ends early.
    /// </summary>
    public static void ReadExact(this Stream stream, byte[] buffer, int offset, int length)
	{
		while (length > 0)
		{
			var read = stream.Read(buffer, offset, length);
			if (read <= 0) throw new IncompleteArchiveException("Unexpected end of stream.");

			offset += read;
			length -= read;
		}
	}

    /// <summary>
    ///     Reads until <paramref name="buffer" /> is full, throwing when the stream ends early.
    ///     Mirrors <c>Stream.ReadExactly(Span&lt;byte&gt;)</c> for target frameworks that do not
    ///     provide it.
    /// </summary>
    public static void ReadExactly(this Stream stream, Span<byte> buffer)
	{
		var total = 0;
		while (total < buffer.Length)
		{
			var read = stream.Read(buffer.Slice(total));
			if (read <= 0) throw new IncompleteArchiveException("Unexpected end of stream.");

			total += read;
		}
	}
}