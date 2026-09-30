// Copyright (c) Metric and the Snowflake Authors.
// Licensed under the Apache License, Version 2.0.

using VCDiff.Includes;

namespace VCDiff.Shared;

internal class AddressCache
{
    /// <summary>
    ///     The address cache implementation as described in the RFC doc.
    /// </summary>
    private const byte DEFAULT_NEAR_CACHE_SIZE = 4;

	private const byte DEFAULT_SAME_CACHE_SIZE = 3;

	public const byte FIRST_NEAR = (byte)VcDiffModes.FIRST;
	private readonly long[] nearCache;
	private readonly byte nearSize;
	private readonly long[] sameCache;
	private readonly byte sameSize;
	private int nextSlot;

	public byte FirstSame => (byte)(VcDiffModes.FIRST + this.nearSize);

	public byte Last => (byte)(this.FirstSame + this.sameSize - 1);

	public static byte DefaultLast => (byte)(VcDiffModes.FIRST + DEFAULT_NEAR_CACHE_SIZE + DEFAULT_SAME_CACHE_SIZE - 1);

	public AddressCache(byte nearSize, byte sameSize)
	{
		this.sameSize = sameSize;
		this.nearSize = nearSize;
		this.nearCache = new long[nearSize];
		this.sameCache = new long[sameSize * 256];
		this.nextSlot = 0;
	}

	public AddressCache()
	{
		this.sameSize = DEFAULT_SAME_CACHE_SIZE;
		this.nearSize = DEFAULT_NEAR_CACHE_SIZE;
		this.nearCache = new long[this.nearSize];
		this.sameCache = new long[this.sameSize * 256];
		this.nextSlot = 0;
	}

	private static bool IsSelfMode(byte mode)
	{
		return mode == (byte)VcDiffModes.SELF;
	}

	private static bool IsHereMode(byte mode)
	{
		return mode == (byte)VcDiffModes.HERE;
	}

	private bool IsNearMode(byte mode)
	{
		return mode >= FIRST_NEAR && mode < this.FirstSame;
	}

	private bool IsSameMode(byte mode)
	{
		return mode >= this.FirstSame && mode <= this.Last;
	}

	private static long DecodeSelfAddress(long encoded)
	{
		return encoded;
	}

	private static long DecodeHereAddress(long encoded, long here)
	{
		return here - encoded;
	}

	private long DecodeNearAddress(byte mode, long encoded)
	{
		return this.NearAddress(mode - FIRST_NEAR) + encoded;
	}

	private long DecodeSameAddress(byte mode, byte encoded)
	{
		return this.SameAddress((mode - this.FirstSame) * 256 + encoded);
	}

	public bool WriteAddressAsVarint(byte mode)
	{
		return !this.IsSameMode(mode);
	}

	private long NearAddress(int pos)
	{
		return this.nearCache[pos];
	}

	private long SameAddress(int pos)
	{
		return this.sameCache[pos];
	}

	private void UpdateCache(long address)
	{
		if (this.nearSize > 0)
		{
			this.nearCache[this.nextSlot] = address;
			this.nextSlot = (this.nextSlot + 1) % this.nearSize;
		}

		if (this.sameSize > 0) this.sameCache[(int)(address % (this.sameSize * 256))] = address;
	}

	public byte EncodeAddress(long address, long here, out long encoded)
	{
		if (address < 0)
		{
			encoded = 0;
			return 0;
		}

		if (address >= here)
		{
			encoded = 0;
			return 0;
		}

		if (this.sameSize > 0)
		{
			var pos = (int)(address % (this.sameSize * 256));
			if (this.SameAddress(pos) == address)
			{
				this.UpdateCache(address);
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

		for (var i = 0; i < this.nearSize; ++i)
		{
			var nearEncoded = address - this.NearAddress(i);
			if (nearEncoded >= 0 && nearEncoded < bestEncoded)
			{
				bestMode = (byte)(FIRST_NEAR + i);
				bestEncoded = nearEncoded;
			}
		}

		this.UpdateCache(address);
		encoded = bestEncoded;
		return bestMode;
	}

	private bool IsDecodedAddressValid(long decoded, long here)
	{
		if (decoded < 0) return false;

		if (decoded >= here) return false;

		return true;
	}

	public long DecodeAddress(long here, byte mode, ByteBuffer sin)
	{
		var start = sin.Position;
		if (here < 0) return (int)VcDiffResult.ERROR;

		if (!sin.CanRead) return (int)VcDiffResult.EOD;

		long decoded = 0;
		if (this.IsSameMode(mode))
		{
			var encoded = sin.ReadByte();
			decoded = this.DecodeSameAddress(mode, encoded);
		}
		else
		{
			var encoded = VarIntBe.ParseInt32(sin);

			switch (encoded)
			{
				case (int)VcDiffResult.ERROR:
					return encoded;

				case (int)VcDiffResult.EOD:
					sin.Position = start;
					return encoded;
			}

			if (IsSelfMode(mode))
				decoded = DecodeSelfAddress(encoded);
			else if (IsHereMode(mode))
				decoded = DecodeHereAddress(encoded, here);
			else if (this.IsNearMode(mode))
				decoded = this.DecodeNearAddress(mode, encoded);
			else
				return (int)VcDiffResult.ERROR;
		}

		if (!this.IsDecodedAddressValid(decoded, here)) return (int)VcDiffResult.ERROR;

		this.UpdateCache(decoded);
		return decoded;
	}
}