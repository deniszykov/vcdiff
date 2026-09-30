// Copyright (c) Metric and the Snowflake Authors.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace VCDiff.Shared;

/// <summary>
///     Encapsulates a buffer that reads bytes from managed or unmanaged memory.
/// </summary>
public class ByteBuffer : IByteBuffer, IDisposable
{
	private MemoryHandle? byteHandle;
	private unsafe byte* bytePtr;

	public bool CanRead
	{
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		get => this.Position < this.Length;
	}

	public long Position
	{
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		get;
		[MethodImpl(MethodImplOptions.AggressiveInlining)]

		// We used to check, but this is never true in calls. if (value > length || value < 0) return;
		set;
	}

	public long Length
	{
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		get;
		private set;
	}

	private ByteBuffer()
	{
	}

    /// <summary />
    public unsafe ByteBuffer(byte[] bytes)
	{
		this.Position = 0;
		var memory = bytes != null ? new Memory<byte>(bytes) : Memory<byte>.Empty;
		this.byteHandle = memory.Pin();
		this.CreateFromPointer((byte*)this.byteHandle.Value.Pointer, memory.Length);
	}

	internal unsafe ByteBuffer(NativeAllocation<byte> bytes)
	{
		this.Position = 0;
		this.CreateFromPointer(bytes.Pointer, bytes.NumItems);
	}

    /// <summary />
    public unsafe ByteBuffer(Memory<byte> bytes)
	{
		this.Position = 0;
		this.byteHandle = bytes.Pin();
		this.CreateFromPointer((byte*)this.byteHandle.Value.Pointer, bytes.Length);
	}

    /// <summary />
    public unsafe ByteBuffer(Span<byte> bytes)
	{
		this.Position = 0;

		// Using GetPinnableReference because length of 0 means out of bound exception.
		this.CreateFromPointer((byte*)Unsafe.AsPointer(ref bytes.GetPinnableReference()), bytes.Length);
	}

    /// <summary />
    public unsafe ByteBuffer(byte* bytes, int length)
	{
		this.Position = 0;
		this.CreateFromPointer(bytes, length);
	}

	~ByteBuffer()
	{
		this.Dispose();
	}

	private unsafe void CreateFromPointer(byte* pointer, long length)
	{
		this.bytePtr = pointer;
		this.Length = length;
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public unsafe Span<byte> AsSpan()
	{
		return MemoryMarshal.CreateSpan(ref Unsafe.AsRef<byte>(this.bytePtr), (int)this.Length);
	}

    /// <summary>
    ///     Dangerously gets the byte pointer.
    /// </summary>
    /// <returns></returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
	public unsafe byte* DangerousGetBytePointer()
	{
		return this.bytePtr;
	}

    /// <summary>
    ///     Dangerously retrieves the byte pointer at the current position and then increases the offset after.
    /// </summary>
    /// <param name="read"></param>
    /// <returns></returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
	public unsafe byte* DangerousGetBytePointerAtCurrentPositionAndIncreaseOffsetAfter(int read)
	{
		var ptr = this.bytePtr + this.Position;
		this.Position += read;
		return ptr;
	}

	[SkipLocalsInit, MethodImpl(MethodImplOptions.AggressiveInlining)]
	public unsafe Span<byte> PeekBytes(int len)
	{
		var sliceLen = (int)(this.Position + len > this.Length ? this.Length - this.Position : len);
		return MemoryMarshal.CreateSpan(ref Unsafe.AsRef<byte>(this.bytePtr + this.Position), sliceLen);
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public unsafe byte PeekByte()
	{
		return *(this.bytePtr + this.Position);
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public Span<byte> ReadBytesToSpan(Span<byte> data)
	{
		var result = this.PeekBytes(data.Length);
		result.CopyTo(data);
		this.Position += result.Length;
		return result.Slice(0, result.Length);
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public unsafe byte ReadByte()
	{
		return this.bytePtr[this.Position++];
	}

	[SkipLocalsInit, MethodImpl(MethodImplOptions.AggressiveInlining)]
	public Span<byte> ReadBytesAsSpan(int len)
	{
		var slice = this.PeekBytes(len);
		this.Position += len;
		return slice;
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public Memory<byte> ReadBytes(int len)
	{
		var slice = this.PeekBytes(len);
		this.Position += len;
		return slice.ToArray();
	}

	public void Next()
	{
		this.Position++;
	}

	public void Dispose()
	{
		this.byteHandle?.Dispose();
		this.byteHandle = null;

		GC.SuppressFinalize(this);
	}
}