// Copyright (c) Denis Zykov and contributors (DeepSeek, ClaudeCode).
// Licensed under the Apache License, Version 2.0.

using System;
using System.Buffers;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.CompilerServices;
using Microsoft.Win32.SafeHandles;

namespace VCDiff.Shared;

/// <summary>
///     Random read access to a dictionary that is mapped from a file as a single read-only
///     <see cref="MemoryMappedViewAccessor" />. The bytes are read in place through a raw pointer and are never
///     copied into managed memory, so a large dictionary does not have to be loaded into the managed heap.
/// </summary>
/// <remarks>
///     <para>
///         The whole file is mapped from offset zero, so <see cref="Length" /> is the file length and offsets are
///         absolute positions within the file (0 .. <see cref="Length" />). The mapping stays valid only until
///         <see cref="Dispose" /> is called; the caller must not read the dictionary after that.
///     </para>
///     <para>
///         The dictionary is limited to 2 GiB (<see cref="int.MaxValue" /> bytes). The limit comes from the
///         encoder's block hash, which indexes blocks with an <see cref="int" />, and from <see cref="Read" />,
///         which addresses the mapped view through an <see cref="int" />-sized <see cref="Memory{T}" /> slice.
///         This matches the existing <see cref="SequenceSourceReader" /> reader.
///     </para>
///     <para>
///         The reader is not thread-safe: one encoder or decoder drives it from a single thread.
///     </para>
/// </remarks>
public sealed unsafe class MemoryMappedFileSourceReader : ISourceReader
{
	private readonly MemoryMappedFile? _file;
	private readonly SafeMemoryMappedViewHandle? _handle;
	private readonly ReadOnlyMemory<byte> _memory;
	private readonly MemoryMappedViewAccessor? _view;
	private byte* _pointer;
	private bool _disposed;

	/// <summary>
	///     The dictionary length in bytes, i.e. the mapped file length.
	/// </summary>
	public long Length { get; }

	/// <summary>
	///     Opens <paramref name="path" /> as a read-only memory-mapped file and maps its entire contents as the
	///     dictionary. This instance owns the mapping and the file, and disposes both.
	/// </summary>
	/// <param name="path">The path of the dictionary file.</param>
	public MemoryMappedFileSourceReader(string path)
	{
		if (path == null)
			throw new ArgumentNullException(nameof(path));

		var length = new FileInfo(path).Length;
		if (length > int.MaxValue)
			throw VcdiffException.DictionaryTooLarge();

		// An empty file can not back a memory-mapped view with zero capacity, so it is handled without mapping.
		if (length == 0)
		{
			this.Length = 0;
			return;
		}

		var file = MemoryMappedFile.CreateFromFile(path, FileMode.Open, null, length, MemoryMappedFileAccess.Read);
		try
		{
			MapView(file, length, out var view, out var handle, out var memory, out var pointer);
			this._file = file;
			this._view = view;
			this._handle = handle;
			this._memory = memory;
			this._pointer = pointer;
			this.Length = length;
		}
		catch
		{
			file.Dispose();
			throw;
		}
	}

	/// <summary>
	///     Maps <c>[0, <paramref name="length" />)</c> of <paramref name="file" /> as the dictionary. This instance
	///     owns the view it creates, and owns <paramref name="file" /> unless <paramref name="leaveOpen" /> is
	///     <see langword="true" />.
	/// </summary>
	/// <param name="file">The memory-mapped file whose start is the dictionary.</param>
	/// <param name="length">The number of bytes to use from the start of <paramref name="file" />.</param>
	/// <param name="leaveOpen">Whether to leave <paramref name="file" /> open when this instance is disposed.</param>
	public MemoryMappedFileSourceReader(MemoryMappedFile file, long length, bool leaveOpen = false)
	{
		if (file == null)
			throw new ArgumentNullException(nameof(file));
		if (length < 0)
			throw new ArgumentOutOfRangeException(nameof(length));
		if (length > int.MaxValue)
			throw VcdiffException.DictionaryTooLarge();

		try
		{
			MapView(file, length, out var view, out var handle, out var memory, out var pointer);
			this._file = leaveOpen ? null : file;
			this._view = view;
			this._handle = handle;
			this._memory = memory;
			this._pointer = pointer;
			this.Length = length;
		}
		catch
		{
			if (!leaveOpen)
				file.Dispose();

			throw;
		}
	}

	/// <summary>
	///     Creates the read-only view over <c>[0, <paramref name="length" />)</c>, acquires its pointer and wraps the
	///     view in the <see cref="ReadOnlyMemory{T}" /> used by <see cref="Read" />. On failure the view it created is
	///     disposed before the exception propagates.
	/// </summary>
	private static void MapView(
		MemoryMappedFile file,
		long length,
		out MemoryMappedViewAccessor? view,
		out SafeMemoryMappedViewHandle? handle,
		out ReadOnlyMemory<byte> memory,
		out byte* pointer)
	{
		memory = default;
		pointer = null;

		// An empty dictionary has no bytes to expose. A zero-byte mapping is not guaranteed to be creatable on
		// every platform, so it is skipped entirely.
		if (length == 0)
		{
			view = null;
			handle = null;
			return;
		}

		view = file.CreateViewAccessor(0, length, MemoryMappedFileAccess.Read);
		try
		{
			handle = view.SafeMemoryMappedViewHandle;
			// The view starts at offset zero, so the pointer points exactly at logical byte zero (no
			// page-alignment delta).
			handle.AcquirePointer(ref pointer);
			memory = new MappedMemoryManager(pointer, (int)length, handle).Memory;
		}
		catch
		{
			view.Dispose();
			throw;
		}
	}

	/// <summary>
	///     Copies <c>destination.Length</c> bytes starting at <paramref name="offset" />. The range must lie within
	///     <see cref="Length" />.
	/// </summary>
	public void CopyTo(long offset, Span<byte> destination)
	{
		if (this._disposed)
			throw new ObjectDisposedException(nameof(MemoryMappedFileSourceReader));

		if (offset < 0 || offset + destination.Length > this.Length)
			throw new ArgumentOutOfRangeException(nameof(offset));

		new ReadOnlySpan<byte>(this._pointer + offset, destination.Length).CopyTo(destination);
	}

	/// <summary>
	///     Whether the <paramref name="length" /> bytes at <paramref name="offset" /> equal the bytes at
	///     <paramref name="other" />.
	/// </summary>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public bool SequenceEqual(long offset, byte* other, int length)
	{
		if (this._disposed)
			throw new ObjectDisposedException(nameof(MemoryMappedFileSourceReader));

		if (offset < 0 || length < 0 || offset + length > this.Length)
			throw new ArgumentOutOfRangeException(nameof(offset));

		return new ReadOnlySpan<byte>(this._pointer + offset, length).SequenceEqual(new ReadOnlySpan<byte>(other, length));
	}

	/// <summary>
	///     Counts how many bytes starting at <paramref name="offset" /> equal the bytes starting at
	///     <paramref name="other" />, up to <paramref name="maxBytes" />.
	/// </summary>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public long MatchForward(long offset, byte* other, long maxBytes)
	{
		if (this._disposed)
			throw new ObjectDisposedException(nameof(MemoryMappedFileSourceReader));

		if (maxBytes < 0)
			throw new ArgumentOutOfRangeException(nameof(maxBytes));
		if (offset < 0 || offset > this.Length)
			throw new ArgumentOutOfRangeException(nameof(offset));

		var remaining = Math.Min(maxBytes, this.Length - offset);
		var span = new ReadOnlySpan<byte>(this._pointer + offset, (int)remaining);
		var otherSpan = new ReadOnlySpan<byte>(other, (int)remaining);
		return ByteComparer.CommonPrefixLength(span, otherSpan);
	}

	/// <summary>
	///     Counts how many bytes before <paramref name="offset" /> equal the bytes before
	///     <paramref name="otherEnd" />, up to <paramref name="maxBytes" />.
	/// </summary>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public long MatchBackward(long offset, byte* otherEnd, long maxBytes)
	{
		if (this._disposed)
			throw new ObjectDisposedException(nameof(MemoryMappedFileSourceReader));

		if (maxBytes < 0)
			throw new ArgumentOutOfRangeException(nameof(maxBytes));
		if (offset < 0 || offset > this.Length)
			throw new ArgumentOutOfRangeException(nameof(offset));

		var remaining = Math.Min(maxBytes, offset);
		var span = new ReadOnlySpan<byte>(this._pointer + offset - remaining, (int)remaining);
		var otherSpan = new ReadOnlySpan<byte>(otherEnd - remaining, (int)remaining);
		return ByteComparer.CommonSuffixLength(span, otherSpan);
	}

	/// <inheritdoc />
	public ReadOnlySequence<byte> Read(long offset, long bytesToRead)
	{
		if (this._disposed)
			throw new ObjectDisposedException(nameof(MemoryMappedFileSourceReader));

		if (offset < 0 || bytesToRead < 0 || offset + bytesToRead > this.Length)
			throw new ArgumentOutOfRangeException(nameof(offset));

		return new ReadOnlySequence<byte>(this._memory.Slice((int)offset, (int)bytesToRead));
	}

	public void Dispose()
	{
		if (this._disposed)
			return;

		this._disposed = true;
		if (this._pointer != null)
		{
			this._handle!.ReleasePointer();
			this._pointer = null;
		}

		this._view?.Dispose();
		this._file?.Dispose();
	}

	/// <summary>
	///     Exposes the mapped region as a <see cref="Memory{T}" /> without copying it. Keeps the view handle alive
	///     and returns the raw pointer from <see cref="MemoryManager{T}.Pin" />.
	/// </summary>
	private sealed unsafe class MappedMemoryManager : MemoryManager<byte>
	{
		private readonly SafeMemoryMappedViewHandle _handle;
		private readonly int _length;
		private readonly byte* _pointer;

		public MappedMemoryManager(byte* pointer, int length, SafeMemoryMappedViewHandle handle)
		{
			this._pointer = pointer;
			this._length = length;
			this._handle = handle;
		}

		public override Span<byte> GetSpan()
		{
			return new Span<byte>(this._pointer, this._length);
		}

		public override MemoryHandle Pin(int elementIndex = 0)
		{
			return new MemoryHandle(this._pointer + elementIndex, default, this);
		}

		public override void Unpin()
		{
		}

		protected override void Dispose(bool disposing)
		{
			// The pointer lifetime is owned by the reader; nothing to release here.
		}
	}
}
