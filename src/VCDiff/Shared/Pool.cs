// Copyright (c) Metric and the Snowflake Authors.
// Licensed under the Apache License, Version 2.0.

using Microsoft.IO;

namespace VCDiff.Shared;

internal static class Pool
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

	public static RecyclableMemoryStreamManager MemoryStreamManager = new(
		new RecyclableMemoryStreamManager.Options(BLOCK_SIZE, LARGE_BUFFER_MULTIPLE, MAX_BUFFER_SIZE, 0, 0) {
			AggressiveBufferReturn = true,
			ThrowExceptionOnToArray = true
		});
}