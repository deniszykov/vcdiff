// Copyright (c) Metric and the Snowflake Authors.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Runtime.InteropServices;

namespace VCDiff.Shared;

/// <summary>
///     An owned block of unmanaged memory holding <see cref="Length" /> items of <typeparamref name="T" />.
/// </summary>
internal unsafe struct NativeAllocation<T> : IDisposable where T : unmanaged
{
	public T* Pointer;
	public readonly int Length;

	public NativeAllocation(int length)
	{
		this.Pointer = (T*)Marshal.AllocHGlobal((IntPtr)((long)length * sizeof(T)));
		this.Length = length;
	}

	public Span<T> AsSpan()
	{
		return new Span<T>(this.Pointer, this.Length);
	}

	public void Dispose()
	{
		if (this.Pointer == null)
			return;

		Marshal.FreeHGlobal((IntPtr)this.Pointer);
		this.Pointer = null;
	}
}
