// Copyright (c) Metric and the Snowflake Authors.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Runtime.InteropServices;

namespace VCDiff.Shared;

internal unsafe struct NativeAllocation<T> : IDisposable where T : unmanaged
{
	public T* Pointer;
	public long NumItems;
	public bool OwnsAllocation;

	public NativeAllocation(long numItems)
	{
		var bytes = numItems * sizeof(T);
		this.Pointer = (T*)Marshal.AllocHGlobal((IntPtr)bytes);
		this.NumItems = numItems;
		this.OwnsAllocation = true;
	}

	public NativeAllocation(IntPtr address, long size) : this()
	{
		this.Pointer = (T*)address;
		this.NumItems = size;
		this.OwnsAllocation = false;
	}

	public Span<byte> AsSpan()
	{
		return new Span<byte>(this.Pointer, (int)this.NumItems);
	}

	public Span<byte> AsSpan(long offset, int length)
	{
		return new Span<byte>(this.Pointer + offset, length);
	}

	public void Dispose()
	{
		if (this.Pointer != (void*)0)
			Marshal.FreeHGlobal((IntPtr)this.Pointer);
	}
}