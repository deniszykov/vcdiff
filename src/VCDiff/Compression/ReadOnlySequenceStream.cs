// Copyright (c) Denis Zykov and contributors (DeepSeek, ClaudeCode).
// Licensed under the Apache License, Version 2.0.

using System;
using System.Buffers;
using System.IO;

namespace VCDiff.Compression;

/// <summary>
///     Read-only, seekable <see cref="Stream" /> over a (possibly multi-segment) <see cref="ReadOnlySequence{T}" />.
///     No data is copied: reads are served segment by segment straight from the sequence memory. The source can be
///     swapped with <see cref="Reset" /> so a single instance can be reused for many inputs.
/// </summary>
internal sealed class ReadOnlySequenceStream : Stream
{
	private SequencePosition _nextSegment;
	private long _position;
	private ReadOnlyMemory<byte> _segment;
	private int _segmentOffset;
	private ReadOnlySequence<byte> _sequence;

	public override bool CanRead => true;
	public override bool CanSeek => true;
	public override bool CanWrite => false;
	public override long Length => this._sequence.Length;

	public override long Position
	{
		get => this._position;
		set
		{
			if (value < 0) throw new ArgumentOutOfRangeException(nameof(value));

			this.SeekTo(value);
		}
	}

	public ReadOnlySequenceStream()
		: this(ReadOnlySequence<byte>.Empty)
	{
	}

	public ReadOnlySequenceStream(ReadOnlySequence<byte> sequence)
	{
		this.Reset(sequence);
	}

	/// <summary>
	///     Replaces the underlying sequence and rewinds to position 0.
	/// </summary>
	public void Reset(ReadOnlySequence<byte> sequence)
	{
		this._sequence = sequence;
		this.SeekTo(0);
	}

	private void SeekTo(long position)
	{
		var length = this._sequence.Length;
		this._nextSegment = this._sequence.Slice(position < length ? position : length).Start;
		this._segment = ReadOnlyMemory<byte>.Empty;
		this._segmentOffset = 0;
		this._position = position;
	}

	private bool LoadNextSegment()
	{
		if (this._position >= this._sequence.Length) return false;

		while (this._sequence.TryGet(ref this._nextSegment, out var memory))
		{
			if (memory.Length == 0) continue;

			this._segment = memory;
			this._segmentOffset = 0;
			return true;
		}

		return false;
	}

	public override int Read(byte[] buffer, int offset, int count)
	{
		return this.Read(buffer.AsSpan(offset, count));
	}

	public override int Read(Span<byte> buffer)
	{
		var total = 0;
		while (total < buffer.Length)
		{
			if (this._segmentOffset >= this._segment.Length && !this.LoadNextSegment()) break;

			var available = Math.Min(this._segment.Length - this._segmentOffset, buffer.Length - total);
			this._segment.Span.Slice(this._segmentOffset, available).CopyTo(buffer.Slice(total));
			this._segmentOffset += available;
			this._position += available;
			total += available;
		}

		return total;
	}

	public override int ReadByte()
	{
		if (this._segmentOffset >= this._segment.Length && !this.LoadNextSegment()) return -1;

		this._position++;
		return this._segment.Span[this._segmentOffset++];
	}

	public override long Seek(long offset, SeekOrigin origin)
	{
		var target = origin switch
		{
			SeekOrigin.Begin => offset,
			SeekOrigin.Current => this._position + offset,
			SeekOrigin.End => this._sequence.Length + offset,
			_ => throw new ArgumentOutOfRangeException(nameof(origin))
		};
		if (target < 0) throw VcdiffException.SeekBeforeBeginOfStream();

		this.SeekTo(target);
		return target;
	}

	public override void Flush()
	{
	}

	public override void SetLength(long value)
	{
		throw new NotSupportedException();
	}

	public override void Write(byte[] buffer, int offset, int count)
	{
		throw new NotSupportedException();
	}
}
