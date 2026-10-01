// Copyright (c) Denis Zykov and contributors (DeepSeek, ClaudeCode).
// Licensed under the Apache License, Version 2.0.

using System;

namespace VCDiff.Compression.Xz;

/// <summary>
///     Forward-only reader over an in-memory, CRC-verified XZ structure (block header). Running past the end means
///     the header's declared fields do not fit its declared size, so it is reported as <see cref="VcdiffException" />.
/// </summary>
internal ref struct XzSpanReader
{
	private readonly ReadOnlySpan<byte> _buffer;

	public int Position { get; private set; }

	public XzSpanReader(ReadOnlySpan<byte> buffer)
	{
		this._buffer = buffer;
		this.Position = 0;
	}

	public byte ReadByte()
	{
		if (this.Position >= this._buffer.Length) throw VcdiffException.XzBlockHeaderTruncated();

		return this._buffer[this.Position++];
	}

	public ReadOnlySpan<byte> ReadBytes(int count)
	{
		if (count < 0 || count > this._buffer.Length - this.Position) throw VcdiffException.XzBlockHeaderTruncated();

		var result = this._buffer.Slice(this.Position, count);
		this.Position += count;
		return result;
	}

	public ulong ReadXzInteger()
	{
		var value = ReadHelpers.ParseXzInteger(this._buffer.Slice(this.Position), out var bytesConsumed);
		this.Position += bytesConsumed;
		return value;
	}
}
