// Copyright (c) Metric and the Snowflake Authors.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Buffers;
using System.Runtime.InteropServices;

namespace VCDiff.Shared;

/// <summary>
///     A byte array rented from an <see cref="ArrayPool{T}" /> together with the number of bytes in use.
///     <see langword="default" /> is an empty, unallocated instance.
/// </summary>
internal struct PooledArray : IDisposable
{
	private readonly ArrayPool<byte> _pool;

	/// <summary>
	///     The rented array (possibly longer than <see cref="Length" />), or <see langword="null" /> when not allocated.
	/// </summary>
	public byte[]? Data { get; private set; }

	/// <summary>
	///     The number of bytes in use.
	/// </summary>
	public int Length { get; }

	public PooledArray(int length, ArrayPool<byte> pool)
	{
		this._pool = pool;
		this.Length = length;
		this.Data = pool.Rent(length);
	}

	/// <summary>
	///     The bytes in use, or an empty span when not allocated.
	/// </summary>
	public Span<byte> AsSpan()
	{
		// The rented array is at least Length long: skip the bounds check of AsSpan(0, Length).
		return this.Data != null ? MemoryMarshal.CreateSpan(ref MemoryMarshal.GetReference(this.Data.AsSpan()), this.Length) : Span<byte>.Empty;
	}

	public void Dispose()
	{
		if (this.Data != null)
		{
			this._pool.Return(this.Data, false);
			this.Data = null;
		}
	}
}
