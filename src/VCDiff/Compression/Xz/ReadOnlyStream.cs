// Portions copyright (c) 2014 Adam Hathcock and the SharpCompress contributors.
// Licensed under the MIT License.

using System;
using System.IO;

namespace VCDiff.Compression.Xz;

/// <summary>
///     Forward-only, non-seekable read-only stream layered over <see cref="BaseStream" /> (which it never owns).
/// </summary>
internal abstract class ReadOnlyStream : Stream
{
	protected Stream BaseStream { get; set; } = null!;

	public override bool CanRead => this.BaseStream.CanRead;

	public override bool CanSeek => false;

	public override bool CanWrite => false;

	public override long Length => throw new NotSupportedException();

	public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

	/// <summary>Base stream is supplied later (see <see cref="Filters.BlockFilter.SetBaseStream" />).</summary>
	protected ReadOnlyStream()
	{
	}

	protected ReadOnlyStream(Stream baseStream)
	{
		this.BaseStream = baseStream;
		if (!baseStream.CanRead) throw new InvalidFormatException("Must be able to read from stream");
	}

	public override int Read(byte[] buffer, int offset, int count)
	{
		return this.Read(buffer.AsSpan(offset, count));
	}

	public abstract override int Read(Span<byte> buffer);

	public override void Flush()
	{
	}

	public override long Seek(long offset, SeekOrigin origin)
	{
		throw new NotSupportedException();
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
