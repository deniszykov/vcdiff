// Copyright (c) Denis Zykov and contributors (DeepSeek, ClaudeCode).
// Licensed under the Apache License, Version 2.0.

using System;

namespace VCDiff;

/// <summary>
///     The category of a <see cref="VcdiffException" />, allowing callers to distinguish failure kinds without
///     catching multiple exception types.
/// </summary>
public enum VcdiffError
{
	/// <summary>The input ended before the expected amount of data was available.</summary>
	Truncated,

	/// <summary>Data was present but malformed, corrupt, or self-inconsistent.</summary>
	InvalidFormat,

	/// <summary>A caller-supplied argument was invalid for the requested operation.</summary>
	InvalidArgument,

	/// <summary>The input is valid, but the feature or configuration is not supported.</summary>
	Unsupported,

	/// <summary>An I/O operation on a stream failed.</summary>
	IoError,

	/// <summary>An internal invariant was violated; this indicates a bug.</summary>
	InternalError,
}

/// <summary>
///     The single exception type thrown by the library for domain, format, and I/O errors. Standard .NET contract
///     exceptions (<see cref="ArgumentNullException" />, <see cref="ArgumentOutOfRangeException" />,
///     <see cref="ObjectDisposedException" />) are still used for plain argument and lifetime checks.
/// </summary>
public sealed class VcdiffException : Exception
{
	public VcdiffException(VcdiffError error, string message)
		: base(message)
	{
		this.Error = error;
	}

	public VcdiffException(VcdiffError error, string message, Exception inner)
		: base(message, inner)
	{
		this.Error = error;
	}

	/// <summary>The category of this failure.</summary>
	public VcdiffError Error { get; }

	// ------------------------------------------------------------------ Truncated

	public static VcdiffException UnexpectedEndOfStream() =>
		new(VcdiffError.Truncated, "Unexpected end of stream.");

	public static VcdiffException TruncatedXzInteger() =>
		new(VcdiffError.Truncated, "Truncated XZ integer");

	public static VcdiffException UnexpectedEndOfLzmaChunk() =>
		new(VcdiffError.Truncated, "Unexpected end of LZMA chunk.");

	public static VcdiffException UnexpectedEndOfLzma2Chunk() =>
		new(VcdiffError.Truncated, "Unexpected end of LZMA2 uncompressed chunk.");

	public static VcdiffException DictionaryStreamEndedEarly() =>
		new(VcdiffError.Truncated, "The dictionary stream ended before its reported length.");

	public static VcdiffException XzBlockHeaderTruncated() =>
		new(VcdiffError.Truncated, "XZ block header truncated");

	// ------------------------------------------------------------------ InvalidFormat

	public static VcdiffException XzIntegerTooLong() =>
		new(VcdiffError.InvalidFormat, "XZ integer too long");

	public static VcdiffException NonCanonicalXzInteger() =>
		new(VcdiffError.InvalidFormat, "Non-canonical XZ integer");

	public static VcdiffException LzmaDataError() =>
		new(VcdiffError.InvalidFormat, "LZMA data error");

	public static VcdiffException InvalidLzmaProperties() =>
		new(VcdiffError.InvalidFormat, "Invalid LZMA properties");

	public static VcdiffException InvalidLzmaDictionarySize(long windowSize) =>
		new(VcdiffError.InvalidFormat, $"LZMA: invalid dictionary size {windowSize}");

	public static VcdiffException InvalidSecondaryCompressedSectionLength() =>
		new(VcdiffError.InvalidFormat, "Invalid secondary-compressed section length");

	public static VcdiffException NonNullPaddingBytes() =>
		new(VcdiffError.InvalidFormat, "Padding bytes were non-null");

	public static VcdiffException BlockCheckCorrupt() =>
		new(VcdiffError.InvalidFormat, "Block check corrupt");

	public static VcdiffException BlockHeaderCorrupt() =>
		new(VcdiffError.InvalidFormat, "Block header corrupt");

	public static VcdiffException ReservedXzBytesUsed() =>
		new(VcdiffError.InvalidFormat, "Reserved bytes used, perhaps an unknown XZ implementation");

	public static VcdiffException BlockFiltersBadOrder() =>
		new(VcdiffError.InvalidFormat, "Block Filters in bad order");

	public static VcdiffException TooManySizeChangingFilters() =>
		new(VcdiffError.InvalidFormat, "More than two non-last block filters cannot change stream size");

	public static VcdiffException BlockHeaderUnknownFields() =>
		new(VcdiffError.InvalidFormat, "Block header contains unknown fields");

	public static VcdiffException FooterCorrupt() =>
		new(VcdiffError.InvalidFormat, "Footer corrupt");

	public static VcdiffException MagicFooterMissing() =>
		new(VcdiffError.InvalidFormat, "Magic footer missing");

	public static VcdiffException StreamFooterFlagsMismatch() =>
		new(VcdiffError.InvalidFormat, "Stream footer flags do not match the header");

	public static VcdiffException StreamFooterBackwardSizeMismatch() =>
		new(VcdiffError.InvalidFormat, "Stream footer backward size does not match the index");

	public static VcdiffException InvalidXzStream() =>
		new(VcdiffError.InvalidFormat, "Invalid XZ Stream");

	public static VcdiffException StreamHeaderCorrupt() =>
		new(VcdiffError.InvalidFormat, "Stream header corrupt");

	public static VcdiffException IndexCorrupt() =>
		new(VcdiffError.InvalidFormat, "Index corrupt");

	public static VcdiffException LzmaPropertiesUnexpectedLength() =>
		new(VcdiffError.InvalidFormat, "LZMA properties unexpected length");

	public static VcdiffException ReservedBitsInLzmaProperties() =>
		new(VcdiffError.InvalidFormat, "Reserved bits used in LZMA properties");

	public static VcdiffException DictionarySizeGreaterThanUInt32Max() =>
		new(VcdiffError.InvalidFormat, "Dictionary size greater than UInt32.Max");

	public static VcdiffException BlockFilterInfoTooLarge() =>
		new(VcdiffError.InvalidFormat, "Block filter information too large");

	public static VcdiffException TargetWindowTooLarge(long maxTargetFileSize) =>
		new(VcdiffError.InvalidFormat, $"Length of a target window exceeds the limit of {maxTargetFileSize} bytes.");

	// ------------------------------------------------------------------ InvalidArgument

	public static VcdiffException StreamIsNotSeekable() =>
		new(VcdiffError.InvalidArgument, "The dictionary stream must be seekable.");

	public static VcdiffException DictionaryTooLarge() =>
		new(VcdiffError.InvalidArgument, "The dictionary can not be larger than 2 GiB.");

	public static VcdiffException MaxTargetFileSizeNotPositive() =>
		new(VcdiffError.InvalidArgument, "MaxTargetFileSize must be a positive value.");

	public static VcdiffException MaxBufferSizeExceeded(int maxMib) =>
		new(VcdiffError.InvalidArgument, $"MaxBufferSize can not exceed {maxMib} MiB.");

	public static VcdiffException BlockSizeInvalid(int blockSize) =>
		new(VcdiffError.InvalidArgument, $"BlockSize must be an even number of at least 2, but is {blockSize}.");

	public static VcdiffException ChunkSizeTooSmall(int minMatchSize, int blockSize) =>
		new(VcdiffError.InvalidArgument, $"ChunkSize ({minMatchSize}) can not be less than twice the BlockSize ({blockSize}).");

	public static VcdiffException RollingHashWindowMismatch() =>
		new(VcdiffError.InvalidArgument, "Supplied RollingHash instance has a different window size than blocksize!");

	public static VcdiffException InterleavedXdelta3ChecksumNotSupported() =>
		new(VcdiffError.InvalidArgument, "Interleaved diffs can not have an xdelta3 checksum!");

	public static VcdiffException StreamingBufferTooLarge() =>
		new(VcdiffError.InvalidArgument, "The streaming buffer can not be larger than 2 GiB.");

	public static VcdiffException EmptyTargetWindow() =>
		new(VcdiffError.InvalidArgument, "Empty target window");

	public static VcdiffException StreamNotReadable() =>
		new(VcdiffError.InvalidArgument, "Must be able to read from stream");

	// ------------------------------------------------------------------ Unsupported

	public static VcdiffException UnsupportedSecondaryCompressor() =>
		new(VcdiffError.Unsupported, "The delta uses a secondary compressor other than xdelta3 LZMA (id 2), which is not supported.");

	public static VcdiffException FilterNotImplemented(ulong filterType) =>
		new(VcdiffError.Unsupported, $"Filter {filterType} has not yet been implemented");

	public static VcdiffException UnsupportedXzCheckSize() =>
		new(VcdiffError.Unsupported, "Unsupported XZ check size");

	public static VcdiffException UnsupportedXzCheckType() =>
		new(VcdiffError.Unsupported, "Unsupported XZ check type");

	public static VcdiffException UnknownXzStreamVersion() =>
		new(VcdiffError.Unsupported, "Unknown XZ Stream Version");

	public static VcdiffException UnknownCheckType() =>
		new(VcdiffError.Unsupported, "Check Type unknown to this version of decoder.");

	// ------------------------------------------------------------------ IoError

	public static VcdiffException SeekBeforeBeginOfStream() =>
		new(VcdiffError.IoError, "An attempt was made to move the position before the beginning of the stream.");

	// ------------------------------------------------------------------ InternalError

	public static VcdiffException BlockHashTableSizeInvalid() =>
		new(VcdiffError.InternalError, "BlockHash Table Size is Invalid == 0");

	public static VcdiffException DeltaOutputLengthMismatch() =>
		new(VcdiffError.InternalError, "Delta output length does not match");
}
