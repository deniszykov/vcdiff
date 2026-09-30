// Portions copyright (c) 2014 Adam Hathcock and the SharpCompress contributors.
// Licensed under the MIT License.

namespace VCDiff.Compression.LZMA.RangeCoder;

internal struct BitEncoder
{
	public const int K_NUM_BIT_MODEL_TOTAL_BITS = 11;
	public const uint K_BIT_MODEL_TOTAL = 1 << K_NUM_BIT_MODEL_TOTAL_BITS;
	private const int K_NUM_MOVE_BITS = 5;
	private const int K_NUM_MOVE_REDUCING_BITS = 2;
	public const int K_NUM_BIT_PRICE_SHIFT_BITS = 6;

	private uint _prob;

	public void Init()
	{
		this._prob = K_BIT_MODEL_TOTAL >> 1;
	}

	public void UpdateModel(uint symbol)
	{
		if (symbol == 0)
			this._prob += (K_BIT_MODEL_TOTAL - this._prob) >> K_NUM_MOVE_BITS;
		else
			this._prob -= this._prob >> K_NUM_MOVE_BITS;
	}

	public void Encode(Encoder encoder, uint symbol)
	{
		var newBound = (encoder.Range >> K_NUM_BIT_MODEL_TOTAL_BITS) * this._prob;
		if (symbol == 0)
		{
			encoder.Range = newBound;
			this._prob += (K_BIT_MODEL_TOTAL - this._prob) >> K_NUM_MOVE_BITS;
		}
		else
		{
			encoder.Low += newBound;
			encoder.Range -= newBound;
			this._prob -= this._prob >> K_NUM_MOVE_BITS;
		}

		if (encoder.Range < Encoder.K_TOP_VALUE)
		{
			encoder.Range <<= 8;
			encoder.ShiftLow();
		}
	}

	private static readonly uint[] ProbPrices = new uint[
		K_BIT_MODEL_TOTAL >> K_NUM_MOVE_REDUCING_BITS
	];

	static BitEncoder()
	{
		const int K_NUM_BITS = K_NUM_BIT_MODEL_TOTAL_BITS - K_NUM_MOVE_REDUCING_BITS;
		for (var i = K_NUM_BITS - 1; i >= 0; i--)
		{
			var start = (uint)1 << (K_NUM_BITS - i - 1);
			var end = (uint)1 << (K_NUM_BITS - i);
			for (var j = start; j < end; j++)
			{
				ProbPrices[j] =
					((uint)i << K_NUM_BIT_PRICE_SHIFT_BITS) + (((end - j) << K_NUM_BIT_PRICE_SHIFT_BITS) >> (K_NUM_BITS - i - 1));
			}
		}
	}

	public uint GetPrice(uint symbol)
	{
		return ProbPrices[
			(((this._prob - symbol) ^ -(int)symbol) & (K_BIT_MODEL_TOTAL - 1)) >> K_NUM_MOVE_REDUCING_BITS
		];
	}

	public uint GetPrice0()
	{
		return ProbPrices[this._prob >> K_NUM_MOVE_REDUCING_BITS];
	}

	public uint GetPrice1()
	{
		return ProbPrices[(K_BIT_MODEL_TOTAL - this._prob) >> K_NUM_MOVE_REDUCING_BITS];
	}
}

internal struct BitDecoder
{
	public const int K_NUM_BIT_MODEL_TOTAL_BITS = 11;
	public const uint K_BIT_MODEL_TOTAL = 1 << K_NUM_BIT_MODEL_TOTAL_BITS;
	private const int K_NUM_MOVE_BITS = 5;

	private uint _prob;

	public void Init()
	{
		this._prob = K_BIT_MODEL_TOTAL >> 1;
	}

	public uint Decode(Decoder rangeDecoder)
	{
		var newBound = (rangeDecoder.Range >> K_NUM_BIT_MODEL_TOTAL_BITS) * this._prob;
		if (rangeDecoder.Code < newBound)
		{
			rangeDecoder.Range = newBound;
			this._prob += (K_BIT_MODEL_TOTAL - this._prob) >> K_NUM_MOVE_BITS;
			rangeDecoder.Normalize2();
			return 0;
		}

		rangeDecoder.Range -= newBound;
		rangeDecoder.Code -= newBound;
		this._prob -= this._prob >> K_NUM_MOVE_BITS;
		rangeDecoder.Normalize2();
		return 1;
	}
}