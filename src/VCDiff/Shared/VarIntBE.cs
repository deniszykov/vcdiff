// Copyright (c) Metric and the Snowflake Authors.
// Licensed under the Apache License, Version 2.0.

using System;
using System.IO;
using VCDiff.Includes;

namespace VCDiff.Shared;

internal class VarIntBe
{
    /// <summary>
    ///     Special VarIntBE class for encoding a Variable BE Integer
    /// </summary>
    public const int INT32_MAX = 5;

	public const int INT32_MAX_VALUE = 0x7FFFFFFF;

	public const int INT64_MAX = 9;
	public const long INT64_MAX_VALUE = 0x7FFFFFFFFFFFFFFF;

	public static int ParseInt32<TByteBufferT>(TByteBufferT sin) where TByteBufferT : IByteBuffer
	{
		var result = 0;
		while (sin.CanRead)
		{
			result += sin.PeekByte() & 0x7f;
			if ((sin.PeekByte() & 0x80) == 0)
			{
				sin.Next();
				return result;
			}

			if (result > INT32_MAX_VALUE >> 7) return (int)VcDiffResult.ERROR;

			result = result << 7;
			sin.Next();
		}

		return (int)VcDiffResult.EOD;
	}

	public static int ParseInt32(ReadOnlySpan<byte> sin, out int bytesConsumed)
	{
		bytesConsumed = 0;
		var result = 0;
		var index = 0;

		while (index < sin.Length)
		{
			var currentByte = sin[index];
			result += currentByte & 0x7f;

			if ((currentByte & 0x80) == 0)
			{
				bytesConsumed = index + 1;
				return result;
			}

			if (result > INT32_MAX_VALUE >> 7)
			{
				bytesConsumed = index + 1;
				return (int)VcDiffResult.ERROR;
			}

			result = result << 7;
			index++;
		}

		return (int)VcDiffResult.EOD;
	}

	public static long ParseInt64(ReadOnlySpan<byte> sin, out int bytesConsumed)
	{
		bytesConsumed = 0;
		long result = 0;
		var index = 0;

		while (index < sin.Length)
		{
			var currentByte = sin[index];
			result += currentByte & 0x7F;

			if ((currentByte & 0x80) == 0)
			{
				bytesConsumed = index + 1;
				return result;
			}

			if (result > INT64_MAX_VALUE >> 7)
			{
				bytesConsumed = index + 1;
				return (long)VcDiffResult.ERROR;
			}

			result = result << 7;
			index++;
		}

		return (long)VcDiffResult.EOD;
	}

	public static long ParseInt64<TByteBufferT>(TByteBufferT sin) where TByteBufferT : IByteBuffer
	{
		long result = 0;
		while (sin.CanRead)
		{
			result += sin.PeekByte() & 0x7F;
			if ((sin.PeekByte() & 0x80) == 0)
			{
				sin.Next();
				return result;
			}

			if (result > INT64_MAX_VALUE >> 7) return (long)VcDiffResult.ERROR;

			result = result << 7;
			sin.Next();
		}

		return (int)VcDiffResult.EOD;
	}

	public static int CalcInt32Length(int v)
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

	public static int CalcInt64Length(long v)
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

	public static int AppendInt32(int v, Stream sout)
	{
		Span<byte> varint = stackalloc byte[INT32_MAX];
		var length = EncodeInt32(v, varint);
		var start = INT32_MAX - length;
		sout.Write(varint[start..INT32_MAX]);
		return length;
	}

	public static int AppendInt64(long v, Stream sout)
	{
		Span<byte> varint = stackalloc byte[INT64_MAX];
		var length = EncodeInt64(v, varint);
		var start = INT64_MAX - length;
		sout.Write(varint[start..INT64_MAX]);
		return length;
	}

	//v cannot be negative!
	//the buffer must be of size: int32Max
	public static int EncodeInt32(int v, Span<byte> sout)
	{
		if (v < 0) return 0;

		var length = 1;
		var idx = INT32_MAX - 1;
		sout[idx] = (byte)(v & 0x7F);
		--idx;
		v >>= 7;
		while (v > 0)
		{
			sout[idx] = (byte)((v & 0x7F) | 0x80);
			--idx;
			++length;
			v >>= 7;
		}

		return length;
	}

	//v cannot be negative!
	//the buffer must be of size: int64Max
	public static int EncodeInt64(long v, Span<byte> sout)
	{
		if (v < 0) return 0;

		var length = 1;
		var idx = INT64_MAX - 1;
		sout[idx] = (byte)(v & 0x7F);
		--idx;
		v >>= 7;
		while (v > 0)
		{
			sout[idx] = (byte)((v & 0x7F) | 0x80);
			--idx;
			++length;
			v >>= 7;
		}

		return length;
	}
}