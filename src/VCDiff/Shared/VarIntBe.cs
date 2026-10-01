// Copyright (c) Metric and the Snowflake Authors.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Buffers;
using System.IO;

namespace VCDiff.Shared;

/// <summary>
///     The RFC 3284 variable-length integer: big-endian base-128, high bit set on every byte but the last.
/// </summary>
internal static class VarIntBe
{
	/// <summary>
	///     The longest encoding of an <see cref="int" /> value.
	/// </summary>
	public const int MAX_INT32_LENGTH = 5;

	/// <summary>
	///     The longest encoding of a <see cref="long" /> value.
	/// </summary>
	public const int MAX_INT64_LENGTH = 9;

	/// <summary>
	///     Parses a varint holding an <see cref="int" />. Returns <see cref="ParseResult.NeedMoreData" /> when
	///     <paramref name="sin" /> ends inside the varint and <see cref="ParseResult.Error" /> when it overflows.
	/// </summary>
	public static ParseResult TryParseInt32(ReadOnlySpan<byte> sin, out int value, out int bytesConsumed)
	{
		bytesConsumed = 0;
		value = 0;
		var result = 0;
		var index = 0;

		while (index < sin.Length)
		{
			var currentByte = sin[index];
			result += currentByte & 0x7f;

			if ((currentByte & 0x80) == 0)
			{
				bytesConsumed = index + 1;
				value = result;
				return ParseResult.Success;
			}

			if (result > int.MaxValue >> 7)
			{
				bytesConsumed = index + 1;
				return ParseResult.Error;
			}

			result = result << 7;
			index++;
		}

		return ParseResult.NeedMoreData;
	}

	/// <summary>
	///     Parses a varint holding an <see cref="int" /> from the start of <paramref name="sequence" />, which may split
	///     it across segments. See <see cref="TryParseInt32(ReadOnlySpan{byte}, out int, out int)" />.
	/// </summary>
	public static ParseResult TryParseInt32(in ReadOnlySequence<byte> sequence, out int value, out int bytesConsumed)
	{
		var first = sequence.FirstSpan;
		if (first.Length >= MAX_INT32_LENGTH || sequence.IsSingleSegment)
			return TryParseInt32(first, out value, out bytesConsumed);

		// The varint straddles segments: consolidate its (at most MAX_INT32_LENGTH) bytes on the stack.
		Span<byte> varint = stackalloc byte[MAX_INT32_LENGTH];
		var length = (int)Math.Min(MAX_INT32_LENGTH, sequence.Length);
		sequence.Slice(0, length).CopyTo(varint);
		return TryParseInt32(varint.Slice(0, length), out value, out bytesConsumed);
	}

	/// <summary>
	///     Parses a varint holding a <see cref="long" />. Returns <see cref="ParseResult.NeedMoreData" /> when
	///     <paramref name="sin" /> ends inside the varint and <see cref="ParseResult.Error" /> when it overflows.
	/// </summary>
	public static ParseResult TryParseInt64(ReadOnlySpan<byte> sin, out long value, out int bytesConsumed)
	{
		bytesConsumed = 0;
		value = 0;
		long result = 0;
		var index = 0;

		while (index < sin.Length)
		{
			var currentByte = sin[index];
			result += currentByte & 0x7F;

			if ((currentByte & 0x80) == 0)
			{
				bytesConsumed = index + 1;
				value = result;
				return ParseResult.Success;
			}

			if (result > long.MaxValue >> 7)
			{
				bytesConsumed = index + 1;
				return ParseResult.Error;
			}

			result = result << 7;
			index++;
		}

		return ParseResult.NeedMoreData;
	}

	/// <summary>
	///     The number of bytes <paramref name="v" /> encodes to; 0 for a negative value, which can not be encoded.
	/// </summary>
	public static int GetLength(long v)
	{
		if (v < 0) return 0;

		var length = 0;
		do
		{
			v >>= 7;
			++length;
		} while (v > 0);

		return length;
	}

	/// <summary>
	///     Writes <paramref name="v" /> at the start of <paramref name="destination" /> and returns the number of bytes
	///     written; a negative value is not written (0 is returned).
	/// </summary>
	public static int Write(long v, Span<byte> destination)
	{
		var length = GetLength(v);
		if (length == 0) return 0;

		destination[length - 1] = (byte)(v & 0x7F);
		for (var i = length - 2; i >= 0; i--)
		{
			v >>= 7;
			destination[i] = (byte)((v & 0x7F) | 0x80);
		}

		return length;
	}

	/// <summary>
	///     Writes <paramref name="v" /> to <paramref name="sout" /> and returns the number of bytes written.
	/// </summary>
	public static int Append(long v, Stream sout)
	{
		Span<byte> varint = stackalloc byte[MAX_INT64_LENGTH];
		var length = Write(v, varint);
		sout.Write(varint.Slice(0, length));
		return length;
	}
}
