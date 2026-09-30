// Copyright (c) Metric and the Snowflake Authors.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Buffers;
using System.IO;
using System.Threading.Tasks;
using VCDiff.Compressors;
using VCDiff.Includes;
using VCDiff.Shared;

namespace VCDiff.Decoders;

/// <summary>
///     Backwards compatibility shim for VCDiff decoder.
///     Please use <see cref="VcDecoderEx{TSourceBuffer,TDeltaBuffer}" /> if you wish to use different stream sources.
/// </summary>
public class VcDecoder : VcDecoderEx<ByteStreamReader, ByteStreamReader>, IDisposable
{
	private readonly bool _ownsSources;
	private bool _disposed;

    /// <summary>
    ///     Creates a new VCDIFF decoder.
    /// </summary>
    /// <param name="source">The dictionary stream, or the base file.</param>
    /// <param name="delta">The stream containing the VCDIFF delta.</param>
    /// <param name="outputStream">The stream to write the output in.</param>
    /// <param name="options">The decoder options. See <see cref="VcDecoderOptions" />.</param>
    public VcDecoder(Stream source, Stream delta, Stream outputStream, VcDecoderOptions options)
		: base(new ByteStreamReader(source, options.BytePool), new ByteStreamReader(delta, options.BytePool), outputStream, options)
	{
		this._ownsSources = true;
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
		(Stream source, Stream delta, Stream outputStream, int maxTargetFileSize = WindowDecoderBase.DEFAULT_MAX_TARGET_FILE_SIZE, bool disableChecksums = false)
		: this(source, delta, outputStream, new VcDecoderOptions { MaxTargetFileSize = maxTargetFileSize, DisableChecksums = disableChecksums })
	{
	}

	/// <inheritdoc />
	public override void Dispose()
	{
		base.Dispose();
		if (this._ownsSources && !this._disposed)
		{
			this.delta.Dispose();
			this.source.Dispose();
			this._disposed = true;
		}

		GC.SuppressFinalize(this);
	}
}

/// <summary>
///     A simple VCDIFF decoder class.
/// </summary>
/// <typeparam name="TSourceBufferT">Type of <see cref="IByteBuffer" /> used for the source buffer.</typeparam>
/// <typeparam name="TDeltaBufferT">Type of <see cref="IByteBuffer" /> used for the delta buffer.</typeparam>
public class VcDecoderEx<TSourceBufferT, TDeltaBufferT> : IDisposable where TSourceBufferT : IByteBuffer
                                                                    where TDeltaBufferT : IByteBuffer
{
	protected static readonly byte[] MagicBytes = { 0xD6, 0xC3, 0xC4, 0x00, 0x00 };
	private readonly ArrayPool<byte> _bytePool;
	private CustomCodeTableDecoder? customTable;
	protected TDeltaBufferT delta;
	protected bool disableChecksums;
	protected int maxTargetFileSize;
	protected Stream outputStream;
	protected TSourceBufferT source;

    /// <summary>
    ///     If the provided delta is in Shared-Dictionary Compression over HTTP (Sandwich) protocol.
    /// </summary>
    public bool IsSdchFormat { get; private set; }

	private byte SecondaryCompressorId { get; set; }

    /// <summary>
    ///     If the decoder has been initialized.
    /// </summary>
    protected bool IsInitialized { get; set; }

    /// <summary>
    ///     Creates a new VCDIFF decoder.
    /// </summary>
    /// <param name="dict">The dictionary stream, or the base file.</param>
    /// <param name="delta">The stream containing the VCDIFF delta.</param>
    /// <param name="outputStream">The stream to write the output in.</param>
    /// <param name="options">The decoder options. See <see cref="VcDecoderOptions" />.</param>
    public VcDecoderEx(TSourceBufferT dict, TDeltaBufferT delta, Stream outputStream, VcDecoderOptions options)
	{
		this.delta = delta;
		this.source = dict;
		this.outputStream = outputStream;
		this.maxTargetFileSize = options.MaxTargetFileSize;
		this.disableChecksums = options.DisableChecksums;
		this._bytePool = options.BytePoolOrDefault;
		this.IsInitialized = false;
	}

    /// <summary>
    ///     Creates a new VCDIFF decoder.
    /// </summary>
    /// <param name="dict">The dictionary stream, or the base file.</param>
    /// <param name="delta">The stream containing the VCDIFF delta.</param>
    /// <param name="outputStream">The stream to write the output in.</param>
    /// <param name="maxTargetFileSize">The maximum target file size (and target window size) in bytes</param>
    /// <param name="disableChecksums">
    ///     Whether to disable checksums when applying the delta. This can be dangerous, but can be
    ///     useful when the input file differs in ways that the delta does not reference.
    /// </param>
    public VcDecoderEx
	(
		TSourceBufferT dict,
		TDeltaBufferT delta,
		Stream outputStream,
		int maxTargetFileSize = WindowDecoderBase.DEFAULT_MAX_TARGET_FILE_SIZE,
		bool disableChecksums = false)
		: this(dict, delta, outputStream, new VcDecoderOptions { MaxTargetFileSize = maxTargetFileSize, DisableChecksums = disableChecksums })
	{
	}

    /// <summary>
    ///     Call this before calling decode
    ///     This expects at least the header part of the delta file
    ///     is available in the stream
    /// </summary>
    /// <returns></returns>
    private VcDiffResult Initialize()
	{
		if (!this.delta.CanRead) return VcDiffResult.EOD;

		var v = this.delta.ReadByte();

		if (!this.delta.CanRead) return VcDiffResult.EOD;

		var c = this.delta.ReadByte();

		if (!this.delta.CanRead) return VcDiffResult.EOD;

		var d = this.delta.ReadByte();

		if (!this.delta.CanRead) return VcDiffResult.EOD;

		var version = this.delta.ReadByte();

		if (!this.delta.CanRead) return VcDiffResult.EOD;

		var hdr = this.delta.ReadByte();

		if (v != MagicBytes[0]) return VcDiffResult.ERROR;

		if (c != MagicBytes[1]) return VcDiffResult.ERROR;

		if (d != MagicBytes[2]) return VcDiffResult.ERROR;

		if (version != 0x00 && version != 'S') return VcDiffResult.ERROR;

		// secondary compression
		if ((hdr & (int)VcDiffCodeFlags.VCDDECOMPRESS) != 0)
		{
			if (!this.delta.CanRead) return VcDiffResult.EOD;

			this.SecondaryCompressorId = this.delta.ReadByte();
		}

		//custom code table!
		if ((hdr & (int)VcDiffCodeFlags.VCDCODETABLE) != 0)
		{
			if (!this.delta.CanRead) return VcDiffResult.EOD;

			//try decoding the custom code table
			//since we don't support the compress the next line should be the length of the code table
			this.customTable = new CustomCodeTableDecoder();
			var result = this.customTable.Decode(this.delta);

			if (result != VcDiffResult.SUCCESS) return result;
		}

		if ((hdr & (int)VcDiffCodeFlags.VCDAPPHEADER) != 0)
		{
			if (!this.delta.CanRead) return VcDiffResult.EOD;

			var headerLength = VarIntBe.ParseInt32(this.delta);

			// skip the app header
			this.delta.ReadBytesAsSpan(headerLength);
		}

		this.IsSdchFormat = version == 'S';

		this.IsInitialized = true;

		return VcDiffResult.SUCCESS;
	}

    /// <summary>
    ///     Writes the patched file into the output stream.
    /// </summary>
    /// <param name="bytesWritten">Number of bytes written into the output stream.</param>
    /// <returns></returns>
    public VcDiffResult Decode(out long bytesWritten)
	{
		if (!this.Decode_Init(out bytesWritten, out var result, out var decodeAsync))
			return result;

		var secondaryCompressor = this.SecondaryCompressorId != 0 ? this.CreateCompressor(this.SecondaryCompressorId) : null;
		try
		{
			while (this.delta.CanRead)
			{
				//delta is streamed in order aka not random access
				using var w = new WindowDecoder<TDeltaBufferT>(this.source.Length, this.delta, secondaryCompressor, this.maxTargetFileSize, this._bytePool);

				if (!w.Decode(this.IsSdchFormat, this.SecondaryCompressorId)) return (VcDiffResult)w.Result;

				using var body = new BodyDecoder<TDeltaBufferT, TSourceBufferT, TDeltaBufferT>(w, this.source, this.delta, this.outputStream,
					disableChecksums: this.disableChecksums);
				if (this.IsSdchFormat && w.AddRunLength == 0 && w.AddressesForCopyLength == 0 && w.InstructionAndSizesLength > 0)
				{
					//interleaved
					//decodedinterleave actually has an internal loop for waiting and streaming the incoming rest of the interleaved window
					result = body.DecodeInterleave();

					if (result != VcDiffResult.SUCCESS && result != VcDiffResult.EOD)
						return result;

					bytesWritten += body.TotalBytesDecoded;
				}

				//technically add could be 0 if it is all copy instructions
				//so do an or check on those two
				else if (!this.IsSdchFormat ||
						(this.IsSdchFormat &&
							(w.AddRunLength > 0 || w.AddressesForCopyLength > 0) &&
							w.InstructionAndSizesLength > 0))
				{
					//not interleaved
					//expects the full window to be available
					//in the stream
					result = body.Decode();
					if (result != VcDiffResult.SUCCESS)
						return result;

					bytesWritten += body.TotalBytesDecoded;
				}
				else
				{
					//invalid file
					return VcDiffResult.ERROR;
				}
			}
		}
		finally
		{
			if (secondaryCompressor is IDisposable secondaryCompressorDisposable) secondaryCompressorDisposable.Dispose();
		}

		return result;
	}

    /// <summary>
    ///     Writes the patched file into the output stream asynchronously.
    ///     This method is only asynchronous for the final step of writing the patched data into the output stream.
    ///     For large outputs, this may be beneficial.
    /// </summary>
    /// <returns></returns>
    public async Task<(VcDiffResult result, long bytesWritten)> DecodeAsync()
	{
		if (!this.Decode_Init(out var bytesWritten, out var result, out var decodeAsync))
			return decodeAsync;

		var secondaryCompressor = this.SecondaryCompressorId != 0 ? this.CreateCompressor(this.SecondaryCompressorId) : null;
		try
		{
			while (this.delta.CanRead)
			{
				//delta is streamed in order aka not random access
				using var w = new WindowDecoder<TDeltaBufferT>(this.source.Length, this.delta, secondaryCompressor, this.maxTargetFileSize, this._bytePool);

				if (w.Decode(this.IsSdchFormat, this.SecondaryCompressorId))
				{
					using var body = new BodyDecoder<TDeltaBufferT, TSourceBufferT, TDeltaBufferT>(w, this.source, this.delta, this.outputStream,
						disableChecksums: this.disableChecksums);
					if (this.IsSdchFormat && w.AddRunLength == 0 && w.AddressesForCopyLength == 0 && w.InstructionAndSizesLength > 0)
					{
						//interleaved
						//decodedinterleave actually has an internal loop for waiting and streaming the incoming rest of the interleaved window
						result = await body.DecodeInterleaveAsync();

						if (result != VcDiffResult.SUCCESS && result != VcDiffResult.EOD)
							return (result, bytesWritten);

						bytesWritten += body.TotalBytesDecoded;
					}

					//technically add could be 0 if it is all copy instructions
					//so do an or check on those two
					else if (!this.IsSdchFormat ||
							(this.IsSdchFormat &&
								(w.AddRunLength > 0 || w.AddressesForCopyLength > 0) &&
								w.InstructionAndSizesLength > 0))
					{
						//not interleaved
						//expects the full window to be available
						//in the stream
						result = await body.DecodeAsync();

						if (result != VcDiffResult.SUCCESS)
							return (result, bytesWritten);

						bytesWritten += body.TotalBytesDecoded;
					}
					else
					{
						//invalid file
						return (VcDiffResult.ERROR, bytesWritten);
					}
				}
				else
					return ((VcDiffResult)w.Result, bytesWritten);
			}
		}
		finally
		{
			if (secondaryCompressor is IDisposable secondaryCompressorDisposable) secondaryCompressorDisposable.Dispose();
		}

		return (result, bytesWritten);
	}

	private bool Decode_Init(out long bytesWritten, out VcDiffResult result, out (VcDiffResult result, long bytesWritten) decodeAsync)
	{
		bytesWritten = 0;
		if (!this.IsInitialized)
		{
			var initializeResult = this.Initialize();
			if (initializeResult != VcDiffResult.SUCCESS || !this.IsInitialized)
			{
				decodeAsync = (initializeResult, bytesWritten);
				result = initializeResult;
				return false;
			}
		}

		result = VcDiffResult.SUCCESS;
		if (!this.delta.CanRead)
		{
			decodeAsync = (VcDiffResult.EOD, bytesWritten);
			return false;
		}

		decodeAsync = default;
		return true;
	}

	private ICompressor? CreateCompressor(byte secondaryCompressorId)
	{
		return secondaryCompressorId switch {
			0 => null,

			// xdelta defines 1 to be "DJW static huffman"
			2 => new XzCompressor(this._bytePool),

			// xdelta defines 16 to be "FGK adaptive huffman" but says it's non-standard
			_ => throw new NotSupportedException($"Secondary compression id '{secondaryCompressorId}' is not supported.")
		};
	}

    /// <summary>
    ///     Disposes the decoder
    /// </summary>
    public virtual void Dispose()
	{
	}
}