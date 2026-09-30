// Portions copyright (c) 2014 Adam Hathcock and the SharpCompress contributors.
// Licensed under the MIT License.

namespace VCDiff.Compression.LZMA.RangeCoder;

internal readonly struct BitTreeEncoder
{
	private readonly BitEncoder[] _models;
	private readonly int _numBitLevels;

	public BitTreeEncoder(int numBitLevels)
	{
		this._numBitLevels = numBitLevels;
		this._models = new BitEncoder[1 << numBitLevels];
	}

	public void Init()
	{
		for (uint i = 1; i < 1 << this._numBitLevels; i++) this._models[i].Init();
	}

	public void Encode(Encoder rangeEncoder, uint symbol)
	{
		uint m = 1;
		for (var bitIndex = this._numBitLevels; bitIndex > 0;)
		{
			bitIndex--;
			var bit = (symbol >> bitIndex) & 1;
			this._models[m].Encode(rangeEncoder, bit);
			m = (m << 1) | bit;
		}
	}

	public void ReverseEncode(Encoder rangeEncoder, uint symbol)
	{
		uint m = 1;
		for (uint i = 0; i < this._numBitLevels; i++)
		{
			var bit = symbol & 1;
			this._models[m].Encode(rangeEncoder, bit);
			m = (m << 1) | bit;
			symbol >>= 1;
		}
	}

	public uint GetPrice(uint symbol)
	{
		uint price = 0;
		uint m = 1;
		for (var bitIndex = this._numBitLevels; bitIndex > 0;)
		{
			bitIndex--;
			var bit = (symbol >> bitIndex) & 1;
			price += this._models[m].GetPrice(bit);
			m = (m << 1) + bit;
		}

		return price;
	}

	public uint ReverseGetPrice(uint symbol)
	{
		uint price = 0;
		uint m = 1;
		for (var i = this._numBitLevels; i > 0; i--)
		{
			var bit = symbol & 1;
			symbol >>= 1;
			price += this._models[m].GetPrice(bit);
			m = (m << 1) | bit;
		}

		return price;
	}

	public static uint ReverseGetPrice
	(
		BitEncoder[] models,
		uint startIndex,
		int numBitLevels,
		uint symbol
	)
	{
		uint price = 0;
		uint m = 1;
		for (var i = numBitLevels; i > 0; i--)
		{
			var bit = symbol & 1;
			symbol >>= 1;
			price += models[startIndex + m].GetPrice(bit);
			m = (m << 1) | bit;
		}

		return price;
	}

	public static void ReverseEncode
	(
		BitEncoder[] models,
		uint startIndex,
		Encoder rangeEncoder,
		int numBitLevels,
		uint symbol
	)
	{
		uint m = 1;
		for (var i = 0; i < numBitLevels; i++)
		{
			var bit = symbol & 1;
			models[startIndex + m].Encode(rangeEncoder, bit);
			m = (m << 1) | bit;
			symbol >>= 1;
		}
	}
}

internal readonly struct BitTreeDecoder
{
	private readonly BitDecoder[] _models;
	private readonly int _numBitLevels;

	public BitTreeDecoder(int numBitLevels)
	{
		this._numBitLevels = numBitLevels;
		this._models = new BitDecoder[1 << numBitLevels];
	}

	public void Init()
	{
		for (uint i = 1; i < 1 << this._numBitLevels; i++) this._models[i].Init();
	}

	public uint Decode(Decoder rangeDecoder)
	{
		uint m = 1;
		for (var bitIndex = this._numBitLevels; bitIndex > 0; bitIndex--) m = (m << 1) + this._models[m].Decode(rangeDecoder);
		return m - ((uint)1 << this._numBitLevels);
	}

	public uint ReverseDecode(Decoder rangeDecoder)
	{
		uint m = 1;
		uint symbol = 0;
		for (var bitIndex = 0; bitIndex < this._numBitLevels; bitIndex++)
		{
			var bit = this._models[m].Decode(rangeDecoder);
			m <<= 1;
			m += bit;
			symbol |= bit << bitIndex;
		}

		return symbol;
	}

	public static uint ReverseDecode
	(
		BitDecoder[] models,
		uint startIndex,
		Decoder rangeDecoder,
		int numBitLevels
	)
	{
		uint m = 1;
		uint symbol = 0;
		for (var bitIndex = 0; bitIndex < numBitLevels; bitIndex++)
		{
			var bit = models[startIndex + m].Decode(rangeDecoder);
			m <<= 1;
			m += bit;
			symbol |= bit << bitIndex;
		}

		return symbol;
	}
}