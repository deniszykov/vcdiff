// Copyright (c) Metric and the Snowflake Authors.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Buffers;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace VCDiff.Shared;

internal struct PinnedArrayRental : IDisposable
{
	private readonly ArrayPool<byte> _pool;

    /// <summary>
    ///     The data encapsulated by this rental.
    /// </summary>
    public byte[]? Data { get; private set; }

    /// <summary>
    ///     The number of bytes in this object.
    /// </summary>
    public int NumBytes { get; }

    /// <summary>
    ///     Converts the data to a span.
    /// </summary>
    public Span<byte> AsSpan()
	{
		return this.Data!.AsSpanFast(this.NumBytes);
	}

	private GCHandle _pin;

    /// <summary>
    ///     Converts the data to a span or an empty span.
    /// </summary>
    public Span<byte> AsSpanOrDefault()
	{
		if (this.Data != null)
			return this.Data.AsSpanFast(this.NumBytes);

		return Span<byte>.Empty;
	}

	public PinnedArrayRental(int numBytes, ArrayPool<byte>? pool = null)
	{
		this._pool = pool ?? ArrayPool<byte>.Shared;
		this.NumBytes = numBytes;
		this.Data = this._pool.Rent(this.NumBytes);
		Debug.Assert(this.Data.Length >= numBytes);
		this._pin = GCHandle.Alloc(this.Data, GCHandleType.Pinned);
	}

	public void Dispose()
	{
		if (this.Data != null)
		{
			this._pin.Free();
			this._pool.Return(this.Data, false);
			this.Data = null;
		}
	}
}