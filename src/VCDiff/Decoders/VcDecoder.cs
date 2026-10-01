// Copyright (c) Metric and the Snowflake Authors.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Buffers;
using System.IO;
using System.Threading.Tasks;
using Microsoft.IO;
using VCDiff.Includes;
using VCDiff.Shared;

namespace VCDiff.Decoders;

/// <summary>
///     A <see cref="Stream" /> based VCDIFF decoder. It reads the delta stream in pooled chunks, decodes it with a
///     <see cref="VcDiffDecoder" /> and writes the target to the output stream.
/// </summary>
/// <remarks>
///     <para>
///         The dictionary (source) stream is addressed by absolute position, from 0 to its
///         <see cref="Stream.Length" />. A <see cref="RecyclableMemoryStream" />, or a <see cref="MemoryStream" />
///         whose buffer is exposable, is read in place and must not be modified while decoding. Any other seekable
///         stream is read on demand (Seek + Read) and is not loaded into memory. A non-seekable stream is first
///         copied into a pooled <see cref="RecyclableMemoryStream" /> from
///         <see cref="VcDecoderOptions.MemoryStreamManager" />.
///     </para>
///     <para>
///         The delta stream is read from its current position to its end and does not have to be seekable. None of
///         the streams are disposed by this class.
///     </para>
/// </remarks>
public class VcDecoder : IDisposable
{
	private const int DELTA_BUFFER_SIZE = 64 * 1024;
	private const int OUTPUT_BUFFER_SIZE = 64 * 1024;

	private readonly Stream _delta;
	private readonly VcDecoderOptions _options;
	private readonly Stream _output;
	private readonly Stream _source;
	private bool _completed;
	private VcDiffDecoder? _decoder;
	private bool _disposed;
	private RecyclableMemoryStream? _sourceCopy;

    /// <summary>
    ///     If the provided delta is in Shared-Dictionary Compression over HTTP (Sandwich) protocol.
    ///     Known once decoding has read the delta header.
    /// </summary>
    public bool IsSdchFormat => this._decoder?.IsSdchFormat ?? false;

    /// <summary>
    ///     Creates a new VCDIFF decoder.
    /// </summary>
    /// <param name="source">The dictionary stream, or the base file.</param>
    /// <param name="delta">The stream containing the VCDIFF delta.</param>
    /// <param name="outputStream">The stream to write the output in.</param>
    /// <param name="options">The decoder options. See <see cref="VcDecoderOptions" />.</param>
    public VcDecoder(Stream source, Stream delta, Stream outputStream, VcDecoderOptions options)
	{
		if (options == null) throw new ArgumentNullException(nameof(options));

		this._source = source ?? throw new ArgumentNullException(nameof(source));
		this._delta = delta ?? throw new ArgumentNullException(nameof(delta));
		this._output = outputStream ?? throw new ArgumentNullException(nameof(outputStream));

		// Snapshot the options so later changes to the caller's instance do not affect this decoder.
		this._options = new VcDecoderOptions {
			MaxTargetFileSize = options.MaxTargetFileSize,
			DisableChecksums = options.DisableChecksums,
			BytePool = options.BytePoolOrDefault,
			MemoryStreamManager = options.MemoryStreamManagerOrDefault
		};
	}

    /// <summary>
    ///     Creates a new VCDIFF decoder.
    /// </summary>
    /// <param name="source">The dictionary stream, or the base file.</param>
    /// <param name="delta">The stream containing the VCDIFF delta.</param>
    /// <param name="outputStream">The stream to write the output in.</param>
    /// <param name="maxTargetFileSize">The maximum target file size (and target window size) in bytes</param>
    /// <param name="disableChecksums">
    ///     Whether to disable checksums when applying the delta. This can be dangerous, but can be
    ///     useful when the input file differs in ways that the delta does not reference.
    /// </param>
    public VcDecoder
		(Stream source, Stream delta, Stream outputStream, int maxTargetFileSize = VcDecoderOptions.DEFAULT_MAX_TARGET_FILE_SIZE, bool disableChecksums = false)
		: this(source, delta, outputStream, new VcDecoderOptions { MaxTargetFileSize = maxTargetFileSize, DisableChecksums = disableChecksums })
	{
	}

    /// <summary>
    ///     Writes the patched file into the output stream.
    /// </summary>
    /// <param name="bytesWritten">Number of bytes written into the output stream.</param>
    /// <returns>
    ///     <see cref="VcDiffResult.SUCCESS" /> when the whole delta was applied, <see cref="VcDiffResult.EOD" /> when the
    ///     delta ended early (or was already decoded) and <see cref="VcDiffResult.ERROR" /> when it is invalid.
    /// </returns>
    /// <exception cref="VcdiffException">The maximum target file size is not positive.</exception>
    /// <exception cref="VcdiffException">A target window is larger than the maximum target file size.</exception>
    /// <exception cref="VcdiffException">The delta uses an unsupported secondary compressor.</exception>
    public VcDiffResult Decode(out long bytesWritten)
	{
		bytesWritten = 0;
		if (!this.CanDecode())
			return VcDiffResult.EOD;

		if (this._decoder == null)
		{
			if (!this._source.CanSeek && this._sourceCopy == null)
				this._source.CopyTo(this.RentSourceCopy());

			this.CreateDecoder();
		}

		var pool = this._options.BytePoolOrDefault;
		var input = pool.Rent(DELTA_BUFFER_SIZE);
		var output = pool.Rent(OUTPUT_BUFFER_SIZE);
		try
		{
			int start = 0, end = 0;
			var endOfDelta = false;
			while (true)
			{
				if (start == end && !endOfDelta)
				{
					start = 0;
					end = Math.Max(0, this._delta.Read(input, 0, input.Length));
					endOfDelta = end == 0;
				}

				var status = this._decoder!.Decode(input.AsSpan(start, end - start), output, out var consumed, out var written, endOfDelta);
				start += consumed;
				if (written > 0)
				{
					this._output.Write(output, 0, written);
					bytesWritten += written;
				}

				if (this.IsFinished(status, endOfDelta, out var result))
					return result;
			}
		}
		finally
		{
			pool.Return(input);
			pool.Return(output);
		}
	}

    /// <summary>
    ///     Writes the patched file into the output stream asynchronously. The delta is read and the target is written
    ///     asynchronously; decoding itself runs synchronously between those operations.
    /// </summary>
    /// <returns>The result (see <see cref="Decode" />) and the number of bytes written into the output stream.</returns>
    public async Task<(VcDiffResult result, long bytesWritten)> DecodeAsync()
	{
		long bytesWritten = 0;
		if (!this.CanDecode())
			return (VcDiffResult.EOD, bytesWritten);

		if (this._decoder == null)
		{
			if (!this._source.CanSeek && this._sourceCopy == null)
				await this._source.CopyToAsync(this.RentSourceCopy()).ConfigureAwait(false);

			this.CreateDecoder();
		}

		var pool = this._options.BytePoolOrDefault;
		var input = pool.Rent(DELTA_BUFFER_SIZE);
		var output = pool.Rent(OUTPUT_BUFFER_SIZE);
		try
		{
			int start = 0, end = 0;
			var endOfDelta = false;
			while (true)
			{
				if (start == end && !endOfDelta)
				{
					start = 0;
					end = Math.Max(0, await this._delta.ReadAsync(input, 0, input.Length).ConfigureAwait(false));
					endOfDelta = end == 0;
				}

				var status = this._decoder!.Decode(input.AsSpan(start, end - start), output, out var consumed, out var written, endOfDelta);
				start += consumed;
				if (written > 0)
				{
					await this._output.WriteAsync(output, 0, written).ConfigureAwait(false);
					bytesWritten += written;
				}

				if (this.IsFinished(status, endOfDelta, out var result))
					return (result, bytesWritten);
			}
		}
		finally
		{
			pool.Return(input);
			pool.Return(output);
		}
	}

	private bool CanDecode()
	{
		if (this._disposed)
			throw new ObjectDisposedException(nameof(VcDecoder));

		// A delta is decoded once; like the end of the delta stream, a further call reports EOD.
		if (this._completed)
			return false;

		if (this._options.MaxTargetFileSize <= 0)
			throw VcdiffException.MaxTargetFileSizeNotPositive();

		// Every call runs the delta to its end, to an error or to an exception: the decoder is single use.
		this._completed = true;
		return true;
	}

	private RecyclableMemoryStream RentSourceCopy()
	{
		var copy = this._options.MemoryStreamManagerOrDefault.GetStream(nameof(VcDecoder));
		this._sourceCopy = copy;
		return copy;
	}

	private void CreateDecoder()
	{
		this._decoder = new VcDiffDecoder(this._options, this.OpenDictionary(), true);
	}

	private IDictionaryReader OpenDictionary()
	{
		var source = this._sourceCopy ?? this._source;

		// In-memory streams are read in place through their buffers.
		if (source is RecyclableMemoryStream recyclable && recyclable.Length <= int.MaxValue)
		{
			// When the source was copied into pooled memory (a non-seekable source), its lifetime is tied to the
			// reader: disposing the decoder disposes the reader, which returns the pooled blocks.
			Action? release = null;
			if (ReferenceEquals(source, this._sourceCopy))
			{
				release = this._sourceCopy!.Dispose;
				this._sourceCopy = null; // ownership moved to the reader
			}

			return new ReadOnlySequenceSource(recyclable.GetReadOnlySequence(), release);
		}

		if (source is MemoryStream memory && memory.TryGetBuffer(out var buffer))
			return new ReadOnlySequenceSource(new ReadOnlySequence<byte>(buffer.Array!, buffer.Offset, buffer.Count));

		return new StreamDictionaryReader(source, this._options.BytePoolOrDefault, this._options.MemoryStreamManagerOrDefault.GetStream());
	}

	private bool IsFinished(OperationStatus status, bool endOfDelta, out VcDiffResult result)
	{
		// ReSharper disable once SwitchStatementHandlesSomeKnownEnumValuesWithDefault
		switch (status)
		{
			case OperationStatus.DestinationTooSmall:
				result = VcDiffResult.SUCCESS;
				return false;
			case OperationStatus.NeedMoreData:
				// The decoder never asks for more after the final input; guard against looping forever anyway.
				result = VcDiffResult.EOD;
				if (!endOfDelta)
					return false;

				break;
			case OperationStatus.Done:
				result = VcDiffResult.SUCCESS;
				break;
			default:
				result = this._decoder!.Failure switch {
					DecodeFailure.Truncated => VcDiffResult.EOD,
					DecodeFailure.TargetWindowTooLarge => throw VcdiffException.TargetWindowTooLarge(this._options.MaxTargetFileSize),
					DecodeFailure.UnsupportedSecondaryCompressor => throw VcdiffException.UnsupportedSecondaryCompressor(),
					_ => VcDiffResult.ERROR
				};
				break;
		}

		return true;
	}

    /// <summary>
    ///     Releases the pooled decoder state. The source, delta and output streams are not disposed.
    /// </summary>
    public virtual void Dispose()
	{
		if (!this._disposed)
		{
			this._disposed = true;
			this._decoder?.Dispose();
			this._decoder = null;
			this._sourceCopy?.Dispose();
			this._sourceCopy = null;
		}

		GC.SuppressFinalize(this);
	}
}
