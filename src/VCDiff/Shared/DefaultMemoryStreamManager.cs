// Copyright (c) Metric and the Snowflake Authors.
// Licensed under the Apache License, Version 2.0.

using Microsoft.IO;

namespace VCDiff.Shared;

/// <summary>
///     Holds the library-wide default <see cref="RecyclableMemoryStreamManager" />, used when the caller does not
///     supply one in the encoder/decoder options.
/// </summary>
internal static class DefaultMemoryStreamManager
{
	private const int BLOCK_SIZE = RecyclableMemoryStreamManager.DefaultBlockSize;

	/// <summary>
	///     For large buffers, multiplies the buffer linearly by this amount.
	/// </summary>
	private const int LARGE_BUFFER_MULTIPLE = 1024 * 1024; // 1 MiB

	/// <summary>
	///     ~2 GiB
	/// </summary>
	private const int MAX_BUFFER_SIZE = LARGE_BUFFER_MULTIPLE * 2047;

	/// <summary>
	///     How much memory the small-block pool is allowed to retain for reuse.
	/// </summary>
	private const long MAX_SMALL_POOL_FREE_BYTES = 16 * 1024 * 1024; // 16 MiB

	/// <summary>
	///     How much memory each large-buffer pool is allowed to retain for reuse.
	/// </summary>
	private const long MAX_LARGE_POOL_FREE_BYTES = 256 * 1024 * 1024; // 256 MiB

	/// <summary>
	///     The shared default <see cref="RecyclableMemoryStreamManager" />.
	/// </summary>
	public static readonly RecyclableMemoryStreamManager Instance = new(
		new RecyclableMemoryStreamManager.Options(BLOCK_SIZE, LARGE_BUFFER_MULTIPLE, MAX_BUFFER_SIZE, MAX_SMALL_POOL_FREE_BYTES, MAX_LARGE_POOL_FREE_BYTES)
		{
			AggressiveBufferReturn = true,
			ThrowExceptionOnToArray = true
		});
}
