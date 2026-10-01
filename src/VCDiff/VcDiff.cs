// Copyright (c) Denis Zykov and contributors (DeepSeek, ClaudeCode).
// Licensed under the Apache License, Version 2.0.

using System;
using System.IO;
using Microsoft.IO;
using VCDiff.Shared;

namespace VCDiff;

/// <summary>
///     Helpers for preparing VCDIFF dictionaries.
/// </summary>
public static class VcDiff
{
    /// <summary>
    ///     Reads an entire dictionary <paramref name="stream" /> into a pooled
    ///     <see cref="RecyclableMemoryStream" />.
    /// </summary>
    /// <param name="stream">The stream containing dictionary data.</param>
    /// <param name="tag">An optional tag used to identify the pooled stream.</param>
    /// <param name="manager">
    ///     An optional <see cref="RecyclableMemoryStreamManager" /> used to rent the returned
    ///     stream. When <see langword="null" />, a shared library-wide default is used.
    /// </param>
    /// <returns>
    ///     A <see cref="RecyclableMemoryStream" /> positioned at the start. The caller owns the
    ///     returned stream and is responsible for disposing it. Pass its
    ///     <see cref="RecyclableMemoryStream.GetReadOnlySequence" /> to a
    ///     <see cref="Encoders.VcDiffEncoder" /> or <see cref="Decoders.VcDiffDecoder" />.
    /// </returns>
    public static RecyclableMemoryStream ReadDictionary(Stream stream, string? tag = null, RecyclableMemoryStreamManager? manager = null)
	{
		if (stream == null)
			throw new ArgumentNullException(nameof(stream));

		var ms = (manager ?? DefaultMemoryStreamManager.Instance).GetStream(tag ?? nameof(VcDiff));
		try
		{
			// Read straight into the pooled blocks of the stream (IBufferWriter), with no
			// intermediate copy buffer.
			while (true)
			{
				var span = ms.GetSpan();
				var read = stream.Read(span);
				if (read <= 0)
					break;

				ms.Advance(read);
			}
		}
		catch
		{
			ms.Dispose();
			throw;
		}

		ms.Position = 0;
		return ms;
	}
}