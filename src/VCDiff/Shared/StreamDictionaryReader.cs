// Copyright (c) Denis Zykov and contributors (DeepSeek, ClaudeCode).
// Licensed under the Apache License, Version 2.0.

using System;
using System.Buffers;
using System.IO;
using Microsoft.IO;

namespace VCDiff.Shared;

/// <summary>
///     Reads the dictionary from a seekable <see cref="Stream" /> on demand (Seek + Read) instead of loading it
///     into memory. Small reads are served from a pooled read cache; reads at least as large as the cache go
///     straight into the destination. Offsets are absolute stream positions (0 .. <see cref="Stream.Length" />).
///     The stream is not owned and is not disposed.
/// </summary>
public sealed unsafe class StreamDictionaryReader : IDictionaryReader
{
	private const int CACHE_SIZE = 32 * 1024;
	private const int MATCH_CHUNK_SIZE = 4096;

	private readonly ArrayPool<byte> _pool;
	private readonly RecyclableMemoryStream _bufferStream;
	private readonly Stream _stream;
	private byte[]? _cache;
	private int _cacheLength;
	private long _cacheOffset;
	private bool _disposed;

	public long Length { get; }

	public StreamDictionaryReader(Stream stream, ArrayPool<byte> pool, RecyclableMemoryStream bufferStream)
	{
		if (!stream.CanSeek)
			throw new ArgumentException("The dictionary stream must be seekable.", nameof(stream));

		this._stream = stream;
		this._pool = pool;
		this._bufferStream = bufferStream;
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

	/// <summary>
	///     Whether the <paramref name="length" /> bytes at <paramref name="offset" /> equal the bytes at
	///     <paramref name="other" />. Unlike <see cref="CopyTo" /> this bypasses the cache: the encoder probes
	///     scattered block offsets, so caching around each probe would read far more than it reuses.
	/// </summary>
	public bool SequenceEqual(long offset, byte* other, int length)
	{
		if (this._disposed)
			throw new ObjectDisposedException(nameof(StreamDictionaryReader));

		if (offset < 0 || length < 0 || offset + length > this.Length)
			throw new ArgumentOutOfRangeException(nameof(offset));

		var otherSpan = new ReadOnlySpan<byte>(other, length);
		if (length <= MATCH_CHUNK_SIZE)
		{
			Span<byte> buffer = stackalloc byte[length];
			this.ReadExact(offset, buffer);
			return buffer.SequenceEqual(otherSpan);
		}

		var rented = this._pool.Rent(length);
		try
		{
			var rentedSpan = rented.AsSpan(0, length);
			this.ReadExact(offset, rentedSpan);
			return rentedSpan.SequenceEqual(otherSpan);
		}
		finally
		{
			this._pool.Return(rented, false);
		}
	}

	/// <summary>
	///     Counts how many bytes starting at <paramref name="offset" /> equal the bytes starting at
	///     <paramref name="other" />, up to <paramref name="maxBytes" />.
	/// </summary>
	public long MatchForward(long offset, byte* other, long maxBytes)
	{
		if (this._disposed)
			throw new ObjectDisposedException(nameof(StreamDictionaryReader));

		if (maxBytes < 0)
			throw new ArgumentOutOfRangeException(nameof(maxBytes));
		if (offset < 0 || offset > this.Length)
			throw new ArgumentOutOfRangeException(nameof(offset));

		var remaining = Math.Min(maxBytes, this.Length - offset);
		var found = 0L;
		Span<byte> buffer = stackalloc byte[MATCH_CHUNK_SIZE];
		while (found < remaining)
		{
			var take = (int)Math.Min(buffer.Length, remaining - found);
			this.ReadExact(offset + found, buffer.Slice(0, take));
			var matched = ByteComparer.CommonPrefixLength(buffer.Slice(0, take), new ReadOnlySpan<byte>(other + found, take));
			found += matched;
			if (matched < take)
				break;
		}

		return found;
	}

	/// <summary>
	///     Counts how many bytes before <paramref name="offset" /> equal the bytes before
	///     <paramref name="otherEnd" />, up to <paramref name="maxBytes" />.
	/// </summary>
	public long MatchBackward(long offset, byte* otherEnd, long maxBytes)
	{
		if (this._disposed)
			throw new ObjectDisposedException(nameof(StreamDictionaryReader));

		if (maxBytes < 0)
			throw new ArgumentOutOfRangeException(nameof(maxBytes));
		if (offset < 0 || offset > this.Length)
			throw new ArgumentOutOfRangeException(nameof(offset));

		var remaining = Math.Min(maxBytes, offset);
		var found = 0L;
		Span<byte> buffer = stackalloc byte[MATCH_CHUNK_SIZE];
		while (found < remaining)
		{
			var take = (int)Math.Min(buffer.Length, remaining - found);
			this.ReadExact(offset - found - take, buffer.Slice(0, take));
			var matched = ByteComparer.CommonSuffixLength(buffer.Slice(0, take), new ReadOnlySpan<byte>(otherEnd - found - take, take));
			found += matched;
			if (matched < take)
				break;
		}

		return found;
	}
	/// <inheritdoc />
	public ReadOnlySequence<byte> Read(long offset, long bytesToRead)
	{
		if (this._disposed)
			throw new ObjectDisposedException(nameof(StreamDictionaryReader));

		if (offset < 0 || bytesToRead < 0 || offset + bytesToRead > this.Length)
			throw new ArgumentOutOfRangeException(nameof(offset));

		this._bufferStream.SetLength(0);
		this._stream.Position = offset;

		while (this._bufferStream.Length < bytesToRead)
		{
			var buffer = this._bufferStream.GetSpan();
			var remaining = bytesToRead - this._bufferStream.Length;
			if (buffer.Length > remaining)
				buffer = buffer.Slice(0, (int)remaining);

			var read = this._stream.Read(buffer);
			if (read <= 0)
				throw new EndOfStreamException("The dictionary stream ended before its reported length.");

			this._bufferStream.Advance(read);
		}

		return this._bufferStream.GetReadOnlySequence();
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
		this._bufferStream.Dispose();
		if (cache != null)
			this._pool.Return(cache, false);
	}
}
