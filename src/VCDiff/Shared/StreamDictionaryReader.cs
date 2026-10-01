// Copyright (c) Denis Zykov and contributors (DeepSeek, ClaudeCode).
// Licensed under the Apache License, Version 2.0.

using System;
using System.Buffers;
using System.IO;

namespace VCDiff.Shared;

/// <summary>
///     Reads the dictionary from a seekable <see cref="Stream" /> on demand (Seek + Read) instead of loading it
///     into memory. Small reads are served from a pooled read cache; reads at least as large as the cache go
///     straight into the destination. Offsets are absolute stream positions (0 .. <see cref="Stream.Length" />).
///     The stream is not owned and is not disposed.
/// </summary>
internal sealed class StreamDictionaryReader : IDictionaryReader
{
	private const int CACHE_SIZE = 32 * 1024;

	private readonly ArrayPool<byte> _pool;
	private readonly Stream _stream;
	private byte[]? _cache;
	private int _cacheLength;
	private long _cacheOffset;
	private bool _disposed;

	public long Length { get; }

	public StreamDictionaryReader(Stream stream, ArrayPool<byte> pool)
	{
		if (!stream.CanSeek)
			throw new ArgumentException("The dictionary stream must be seekable.", nameof(stream));

		this._stream = stream;
		this._pool = pool;
		this.Length = stream.Length;
	}

	public void CopyTo(long offset, Span<byte> destination)
	{
		if (this._disposed)
			throw new ObjectDisposedException(nameof(StreamDictionaryReader));

		if (offset < 0 || offset + destination.Length > this.Length)
			throw new ArgumentOutOfRangeException(nameof(offset));

		// Served (at least partially) from the cache.
		if (this._cache != null && offset >= this._cacheOffset && offset < this._cacheOffset + this._cacheLength)
		{
			var start = (int)(offset - this._cacheOffset);
			var take = Math.Min(this._cacheLength - start, destination.Length);
			this._cache.AsSpan(start, take).CopyTo(destination);
			destination = destination.Slice(take);
			offset += take;
		}

		if (destination.IsEmpty)
			return;

		if (destination.Length >= CACHE_SIZE)
		{
			this.ReadExact(offset, destination);
			return;
		}

		var cache = this._cache ??= this._pool.Rent(CACHE_SIZE);
		var length = (int)Math.Min(cache.Length, this.Length - offset);

		// Invalidate first: if the read throws, the cache must not claim stale content.
		this._cacheLength = 0;
		this.ReadExact(offset, cache.AsSpan(0, length));
		this._cacheOffset = offset;
		this._cacheLength = length;
		cache.AsSpan(0, destination.Length).CopyTo(destination);
	}

	private void ReadExact(long offset, Span<byte> destination)
	{
		if (this._stream.Position != offset)
			this._stream.Position = offset;

		while (!destination.IsEmpty)
		{
			var read = this._stream.Read(destination);
			if (read <= 0)
				throw new EndOfStreamException("The dictionary stream ended before its reported length.");

			destination = destination.Slice(read);
		}
	}

	public void Dispose()
	{
		if (this._disposed)
			return;

		this._disposed = true;
		var cache = this._cache;
		this._cache = null;
		this._cacheLength = 0;
		if (cache != null)
			this._pool.Return(cache, false);
	}
}
