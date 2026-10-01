// Copyright (c) Metric and the Snowflake Authors.
// Licensed under the Apache License, Version 2.0.

using System;
using VCDiff.Includes;

namespace VCDiff.Shared;

/// <summary>
///     The RFC 3284 address cache (section 5.1) with the NEAR and SAME caches. The encoder uses it to pick the
///     cheapest addressing mode of a COPY address, the decoder to resolve an encoded address. It is reused for every
///     window of a delta: call <see cref="Reset" /> before each one.
/// </summary>
internal sealed class AddressCache
{
	private const byte DEFAULT_NEAR_SIZE = 4;
	private const byte DEFAULT_SAME_SIZE = 3;

	private const byte FIRST_NEAR = (byte)VcDiffModes.FIRST;

	private readonly long[] _nearCache;
	private readonly byte _nearSize;
	private readonly long[] _sameCache;
	private readonly byte _sameSize;
	private int _nextSlot;

	/// <summary>
	///     The last address mode of the default cache sizes.
	/// </summary>
	public const byte DEFAULT_LAST = (byte)VcDiffModes.FIRST + DEFAULT_NEAR_SIZE + DEFAULT_SAME_SIZE - 1;

	private byte FirstSame => (byte)(FIRST_NEAR + this._nearSize);

	private byte Last => (byte)(this.FirstSame + this._sameSize - 1);

	public AddressCache(byte nearSize = DEFAULT_NEAR_SIZE, byte sameSize = DEFAULT_SAME_SIZE)
	{
		this._nearSize = nearSize;
		this._sameSize = sameSize;
		this._nearCache = new long[nearSize];
		this._sameCache = new long[sameSize * 256];
	}

	/// <summary>
	///     Clears the cache at the start of a window.
	/// </summary>
	public void Reset()
	{
		Array.Clear(this._nearCache, 0, this._nearCache.Length);
		Array.Clear(this._sameCache, 0, this._sameCache.Length);
		this._nextSlot = 0;
	}

	/// <summary>
	///     Whether addresses of <paramref name="mode" /> are a single byte rather than a varint.
	/// </summary>
	public bool IsSameMode(byte mode)
	{
		return mode >= this.FirstSame && mode <= this.Last;
	}

	private void Update(long address)
	{
		if (this._nearSize > 0)
		{
			this._nearCache[this._nextSlot] = address;
			this._nextSlot = (this._nextSlot + 1) % this._nearSize;
		}

		if (this._sameSize > 0) this._sameCache[(int)(address % (this._sameSize * 256))] = address;
	}

	/// <summary>
	///     Picks the addressing mode that encodes <paramref name="address" /> (which must be below
	///     <paramref name="here" />) in the fewest bytes and updates the cache.
	/// </summary>
	/// <returns>The address mode; <paramref name="encoded" /> receives the value to write for it.</returns>
	public byte EncodeAddress(long address, long here, out long encoded)
	{
		if (address < 0 || address >= here)
		{
			encoded = 0;
			return 0;
		}

		if (this._sameSize > 0)
		{
			var pos = (int)(address % (this._sameSize * 256));
			if (this._sameCache[pos] == address)
			{
				this.Update(address);
				encoded = pos % 256;
				return (byte)(this.FirstSame + pos / 256);
			}
		}

		var bestMode = (byte)VcDiffModes.SELF;
		var bestEncoded = address;

		var hereEncoded = here - address;
		if (hereEncoded < bestEncoded)
		{
			bestMode = (byte)VcDiffModes.HERE;
			bestEncoded = hereEncoded;
		}

		for (var i = 0; i < this._nearSize; ++i)
		{
			var nearEncoded = address - this._nearCache[i];
			if (nearEncoded >= 0 && nearEncoded < bestEncoded)
			{
				bestMode = (byte)(FIRST_NEAR + i);
				bestEncoded = nearEncoded;
			}
		}

		this.Update(address);
		encoded = bestEncoded;
		return bestMode;
	}

	/// <summary>
	///     Decodes the address of a COPY with <paramref name="mode" />, reading its encoded value from
	///     <paramref name="data" /> at <paramref name="index" /> (advanced past it), and updates the cache.
	/// </summary>
	/// <returns>
	///     The address; <paramref name="status" /> is <see cref="VcDiffResult.EOD" /> when <paramref name="data" /> ends
	///     before the encoded value and <see cref="VcDiffResult.ERROR" /> when the address is invalid (it must be below
	///     <paramref name="here" />). The cache is only updated on success.
	/// </returns>
	public long DecodeAddress(long here, byte mode, ReadOnlySpan<byte> data, ref int index, out VcDiffResult status)
	{
		status = VcDiffResult.SUCCESS;

		long decoded;
		if (this.IsSameMode(mode))
		{
			if (index >= data.Length)
			{
				status = VcDiffResult.EOD;
				return 0;
			}

			var encoded = data[index++];
			decoded = this._sameCache[(mode - this.FirstSame) * 256 + encoded];
		}
		else
		{
			var parsed = VarIntBe.ParseInt32(data.Slice(index), out var vb);
			if (parsed < 0)
			{
				status = (VcDiffResult)parsed; // ERROR or EOD
				return 0;
			}

			index += vb;
			if (mode == (byte)VcDiffModes.SELF)
				decoded = parsed;
			else if (mode == (byte)VcDiffModes.HERE)
				decoded = here - parsed;
			else if (mode >= FIRST_NEAR && mode < this.FirstSame)
				decoded = this._nearCache[mode - FIRST_NEAR] + parsed;
			else
			{
				status = VcDiffResult.ERROR;
				return 0;
			}
		}

		if (decoded < 0 || decoded >= here)
		{
			status = VcDiffResult.ERROR;
			return 0;
		}

		this.Update(decoded);
		return decoded;
	}
}
