// Copyright (c) Metric and the Snowflake Authors.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Buffers;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace VCDiff.Shared;

//Wrapper Class for any stream that supports Position
//and Length to make reading bytes easier
//also has a helper function for reading all the bytes in at once
public class ByteStreamReader : IByteBuffer
{
	private const int CACHE_SIZE = 8192;
	private readonly ArrayPool<byte> _pool;

	private readonly Stream buffer;
	private readonly byte[] cache;
	private bool _isDisposed;
	private int lastLenRead;

	public long Position
	{
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		get => this.buffer.Position;
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		set => this.buffer.Seek(value, SeekOrigin.Begin);
	}

	public long Length
	{
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		get => this.buffer.CanRead ? this.buffer.Length : 0;
	}

	public bool CanRead
	{
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		get => this.buffer.CanRead && this.buffer.Position < this.buffer.Length;
	}

	public ByteStreamReader(Stream stream, ArrayPool<byte>? bytePool = null)
	{
		this._pool = bytePool ?? ArrayPool<byte>.Shared;
		this.cache = this._pool.Rent(CACHE_SIZE);
		this.buffer = stream;
	}

	~ByteStreamReader()
	{
		this.Dispose();
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public int ReadBytesIntoBuf(Span<byte> buf)
	{
		var actualRead = this.buffer.Read(buf);
		this.lastLenRead = actualRead;
		return actualRead;
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public async Task<int> ReadBytesIntoBufAsync(Memory<byte> buf)
	{
		var actualRead = await this.buffer.ReadAsync(buf);
		this.lastLenRead = actualRead;
		return actualRead;
	}

	[SkipLocalsInit, MethodImpl(MethodImplOptions.AggressiveOptimization | MethodImplOptions.AggressiveInlining)]
	private byte[] GetCachedBuffer(int len)
	{
		if (len <= CACHE_SIZE)
			return this.cache;

#if NET5_0_OR_GREATER
		return GC.AllocateUninitializedArray<byte>(len);
#else
            return new byte[len];
#endif
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public Span<byte> ReadBytesToSpan(Span<byte> data)
	{
		var bytesRead = this.buffer.Read(data);
		return data.Slice(0, bytesRead);
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public byte ReadByte()
	{
		this.lastLenRead = this.buffer.ReadByte();
		if (this.lastLenRead > -1)
			return (byte)this.lastLenRead;

		return 0;
	}

	[SkipLocalsInit, MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
	public Span<byte> ReadBytesAsSpan(int len)
	{
		var buf = this.GetCachedBuffer(len);
		var actualRead = this.buffer.Read(buf.AsSpanFast(len));
		this.lastLenRead = actualRead;
		return actualRead > 0 ? buf.AsSpanFast(actualRead) : Span<byte>.Empty;
	}

	[SkipLocalsInit, MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
	public Memory<byte> ReadBytes(int len)
	{
		var buf = this.GetCachedBuffer(len);
		var actualRead = this.buffer.Read(buf.AsSpanFast(len));
		this.lastLenRead = actualRead;
		return actualRead > 0 ? buf.AsMemory(0, actualRead) : Memory<byte>.Empty;
	}

	public byte PeekByte()
	{
		var b = this.ReadByte();
		this.buffer.Seek(-1, SeekOrigin.Current);
		return b;
	}

	//increases the offset by 1
	public void Next()
	{
		this.buffer.Seek(1, SeekOrigin.Current);
	}

	public void Dispose()
	{
		if (!this._isDisposed)
		{
			this._pool.Return(this.cache, false);
			this._isDisposed = true;
		}

		GC.SuppressFinalize(this);
	}
}