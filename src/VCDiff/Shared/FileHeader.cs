// Copyright (c) Denis Zykov and contributors (DeepSeek, ClaudeCode).
// Licensed under the Apache License, Version 2.0.

using System;

namespace VCDiff.Shared;

/// <summary>
///     The fixed start of a VCDIFF delta (RFC 3284 section 4.1): the three magic bytes, the version byte and the
///     Hdr_Indicator byte.
/// </summary>
internal static class FileHeader
{
	/// <summary>
	///     The version byte of an RFC 3284 delta.
	/// </summary>
	public const byte VERSION_RFC3284 = 0x00;

	/// <summary>
	///     The version byte of a delta using the open-vcdiff SDCH extensions (interleaving, SDCH checksums).
	/// </summary>
	public const byte VERSION_SDCH = (byte)'S';

	/// <summary>
	///     The length of the magic bytes plus the version and Hdr_Indicator bytes.
	/// </summary>
	public const int LENGTH = 5;

	private static readonly byte[] Rfc3284 = { 0xD6, 0xC3, 0xC4, VERSION_RFC3284, 0x00 };
	private static readonly byte[] Sdch = { 0xD6, 0xC3, 0xC4, VERSION_SDCH, 0x00 };

	/// <summary>
	///     The magic bytes 'V' 'C' 'D' with their high bits set.
	/// </summary>
	public static ReadOnlySpan<byte> Magic => new ReadOnlySpan<byte>(Rfc3284, 0, 3);

	/// <summary>
	///     The header, without application data, secondary compressor or custom code table, written by the encoders.
	/// </summary>
	public static ReadOnlyMemory<byte> Get(bool sdch)
	{
		return sdch ? Sdch : Rfc3284;
	}
}
