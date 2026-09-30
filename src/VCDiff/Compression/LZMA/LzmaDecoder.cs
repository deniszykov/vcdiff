// Portions copyright (c) 2014 Adam Hathcock and the SharpCompress contributors.
// Licensed under the MIT License.

using System;
using System.IO;
using VCDiff.Compression.LZMA.LZ;
using VCDiff.Compression.LZMA.RangeCoder;

namespace VCDiff.Compression.LZMA;

public partial class Decoder : ICoder, ISetDecoderProperties, IDisposable
{
	private class LenDecoder
	{
		private readonly BitTreeDecoder _highCoder = new(Base.K_NUM_HIGH_LEN_BITS);
		private readonly BitTreeDecoder[] _lowCoder = new BitTreeDecoder[Base.K_NUM_POS_STATES_MAX];
		private readonly BitTreeDecoder[] _midCoder = new BitTreeDecoder[Base.K_NUM_POS_STATES_MAX];
		private BitDecoder _choice;
		private BitDecoder _choice2;
		private uint _numPosStates;

		public void Create(uint numPosStates)
		{
			for (var posState = this._numPosStates; posState < numPosStates; posState++)
			{
				this._lowCoder[posState] = new BitTreeDecoder(Base.K_NUM_LOW_LEN_BITS);
				this._midCoder[posState] = new BitTreeDecoder(Base.K_NUM_MID_LEN_BITS);
			}

			this._numPosStates = numPosStates;
		}

		public void Init()
		{
			this._choice.Init();
			for (uint posState = 0; posState < this._numPosStates; posState++)
			{
				this._lowCoder[posState].Init();
				this._midCoder[posState].Init();
			}

			this._choice2.Init();
			this._highCoder.Init();
		}

		public uint Decode(RangeCoder.Decoder rangeDecoder, uint posState)
		{
			if (this._choice.Decode(rangeDecoder) == 0) return this._lowCoder[posState].Decode(rangeDecoder);

			var symbol = Base.K_NUM_LOW_LEN_SYMBOLS;
			if (this._choice2.Decode(rangeDecoder) == 0)
				symbol += this._midCoder[posState].Decode(rangeDecoder);
			else
			{
				symbol += Base.K_NUM_MID_LEN_SYMBOLS;
				symbol += this._highCoder.Decode(rangeDecoder);
			}

			return symbol;
		}
	}

	private partial class LiteralDecoder
	{
		private struct Decoder2
		{
			private BitDecoder[] _decoders;
			private int _baseIndex;

			public void Create(BitDecoder[] decoders, int baseIndex)
			{
				this._decoders = decoders;
				this._baseIndex = baseIndex;
			}

			public void Init()
			{
				for (var i = 0; i < 0x300; i++) this._decoders[this._baseIndex + i].Init();
			}

			public byte DecodeNormal(RangeCoder.Decoder rangeDecoder)
			{
				uint symbol = 1;
				do
				{
					symbol = (symbol << 1) | this._decoders[this._baseIndex + symbol].Decode(rangeDecoder);
				} while (symbol < 0x100);

				return (byte)symbol;
			}

			public byte DecodeWithMatchByte(RangeCoder.Decoder rangeDecoder, byte matchByte)
			{
				uint symbol = 1;
				do
				{
					var matchBit = (uint)(matchByte >> 7) & 1;
					matchByte <<= 1;
					var bit = this._decoders[this._baseIndex + ((1 + matchBit) << 8) + symbol]
						.Decode(rangeDecoder);
					symbol = (symbol << 1) | bit;
					if (matchBit != bit)
					{
						while (symbol < 0x100)
						{
							symbol =
								(symbol << 1) | this._decoders[this._baseIndex + symbol].Decode(rangeDecoder);
						}

						break;
					}
				} while (symbol < 0x100);

				return (byte)symbol;
			}
		}

		private Decoder2[] _coders = null!;
		private BitDecoder[] _models = null!;
		private int _numPosBits;
		private int _numPrevBits;
		private uint _posMask;

		public void Create(int numPosBits, int numPrevBits)
		{
			if (this._coders != null && this._numPrevBits == numPrevBits && this._numPosBits == numPosBits) return;

			this._numPosBits = numPosBits;
			this._posMask = ((uint)1 << numPosBits) - 1;
			this._numPrevBits = numPrevBits;
			var numStates = (uint)1 << (this._numPrevBits + this._numPosBits);
			this._models = new BitDecoder[checked((int)(numStates * 0x300))];
			this._coders = new Decoder2[numStates];
			for (uint i = 0; i < numStates; i++) this._coders[i].Create(this._models, checked((int)(i * 0x300)));
		}

		public void Init()
		{
			var numStates = (uint)1 << (this._numPrevBits + this._numPosBits);
			for (uint i = 0; i < numStates; i++) this._coders[i].Init();
		}

		private uint GetState(uint pos, byte prevByte)
		{
			return ((pos & this._posMask) << this._numPrevBits) + (uint)(prevByte >> (8 - this._numPrevBits));
		}

		public byte DecodeNormal(RangeCoder.Decoder rangeDecoder, uint pos, byte prevByte)
		{
			return this._coders[this.GetState(pos, prevByte)].DecodeNormal(rangeDecoder);
		}

		public byte DecodeWithMatchByte
		(
			RangeCoder.Decoder rangeDecoder,
			uint pos,
			byte prevByte,
			byte matchByte
		)
		{
			return this._coders[this.GetState(pos, prevByte)].DecodeWithMatchByte(rangeDecoder, matchByte);
		}
	}

	private readonly BitDecoder[] _isMatchDecoders = new BitDecoder[
		Base.K_NUM_STATES << Base.K_NUM_POS_STATES_BITS_MAX
	];
	private readonly BitDecoder[] _isRep0LongDecoders = new BitDecoder[
		Base.K_NUM_STATES << Base.K_NUM_POS_STATES_BITS_MAX
	];
	private readonly BitDecoder[] _isRepDecoders = new BitDecoder[Base.K_NUM_STATES];
	private readonly BitDecoder[] _isRepG0Decoders = new BitDecoder[Base.K_NUM_STATES];
	private readonly BitDecoder[] _isRepG1Decoders = new BitDecoder[Base.K_NUM_STATES];
	private readonly BitDecoder[] _isRepG2Decoders = new BitDecoder[Base.K_NUM_STATES];

	private readonly LenDecoder _lenDecoder = new();

	private readonly LiteralDecoder _literalDecoder = new();

	private readonly BitTreeDecoder _posAlignDecoder = new(Base.K_NUM_ALIGN_BITS);
	private readonly BitDecoder[] _posDecoders = new BitDecoder[
		Base.K_NUM_FULL_DISTANCES - Base.K_END_POS_MODEL_INDEX
	];

	private readonly BitTreeDecoder[] _posSlotDecoder = new BitTreeDecoder[
		Base.K_NUM_LEN_TO_POS_STATES
	];
	private readonly LenDecoder _repLenDecoder = new();

	private int _dictionarySize;

	private OutWindow? _outWindow;

	private uint _posStateMask;
	private uint _rep0,
		_rep1,
		_rep2,
		_rep3;

	private Base.State _state;
	internal bool HasEndMarker => this._rep0 == uint.MaxValue;

	public Decoder()
	{
		this._dictionarySize = -1;
		for (var i = 0; i < Base.K_NUM_LEN_TO_POS_STATES; i++) this._posSlotDecoder[i] = new BitTreeDecoder(Base.K_NUM_POS_SLOT_BITS);
	}

	private void CreateDictionary()
	{
		if (this._dictionarySize < 0) throw new InvalidParamException();

		this._outWindow = new OutWindow();
		var blockSize = Math.Max(this._dictionarySize, 1 << 12);
		this._outWindow.Create(blockSize);
	}

	private void SetLiteralProperties(int lp, int lc)
	{
		if (lp > 8) throw new InvalidParamException();

		if (lc > 8) throw new InvalidParamException();

		this._literalDecoder.Create(lp, lc);
	}

	private void SetPosBitsProperties(int pb)
	{
		if (pb > Base.K_NUM_POS_STATES_BITS_MAX) throw new InvalidParamException();

		var numPosStates = (uint)1 << pb;
		this._lenDecoder.Create(numPosStates);
		this._repLenDecoder.Create(numPosStates);
		this._posStateMask = numPosStates - 1;
	}

	private void Init()
	{
		uint i;
		for (i = 0; i < Base.K_NUM_STATES; i++)
		{
			for (uint j = 0; j <= this._posStateMask; j++)
			{
				var index = (i << Base.K_NUM_POS_STATES_BITS_MAX) + j;
				this._isMatchDecoders[index].Init();
				this._isRep0LongDecoders[index].Init();
			}

			this._isRepDecoders[i].Init();
			this._isRepG0Decoders[i].Init();
			this._isRepG1Decoders[i].Init();
			this._isRepG2Decoders[i].Init();
		}

		this._literalDecoder.Init();
		for (i = 0; i < Base.K_NUM_LEN_TO_POS_STATES; i++) this._posSlotDecoder[i].Init();

		// _PosSpecDecoder.Init();
		for (i = 0; i < Base.K_NUM_FULL_DISTANCES - Base.K_END_POS_MODEL_INDEX; i++) this._posDecoders[i].Init();

		this._lenDecoder.Init();
		this._repLenDecoder.Init();
		this._posAlignDecoder.Init();

		this._state.Init();
		this._rep0 = 0;
		this._rep1 = 0;
		this._rep2 = 0;
		this._rep3 = 0;
	}

	internal bool Code(int dictionarySize, OutWindow outWindow, RangeCoder.Decoder rangeDecoder)
	{
		return this.CodeFast(dictionarySize, outWindow, rangeDecoder);
	}

	internal void SetDecoderProperties(ReadOnlySpan<byte> properties)
	{
		if (properties.Length < 1) throw new InvalidParamException();

		var lc = properties[0] % 9;
		var remainder = properties[0] / 9;
		var lp = remainder % 5;
		var pb = remainder / 5;
		if (pb > Base.K_NUM_POS_STATES_BITS_MAX) throw new InvalidParamException();

		this.SetLiteralProperties(lp, lc);
		this.SetPosBitsProperties(pb);
		this.Init();
		this.CreateFastModel(lp, lc);
		this.InitFastModel();
		if (properties.Length >= 5)
		{
			this._dictionarySize = 0;
			for (var i = 0; i < 4; i++) this._dictionarySize += properties[1 + i] << (i * 8);
		}
	}

	public void Train(Stream stream)
	{
		if (this._outWindow is null) this.CreateDictionary();

		this._outWindow!.Train(stream);
	}

	public void Code
	(
		Stream inStream,
		Stream outStream,
		long inSize,
		long outSize,
		ICodeProgress progress
	)
	{
		if (this._outWindow is null) this.CreateDictionary();

		this._outWindow!.Init(outStream);
		if (outSize > 0)
			this._outWindow!.SetLimit(outSize);
		else
			this._outWindow!.SetLimit(long.MaxValue - this._outWindow!.Total);

		var rangeDecoder = new RangeCoder.Decoder();
		rangeDecoder.Init(inStream);

		this.Code(this._dictionarySize, this._outWindow!, rangeDecoder);

		this._outWindow!.ReleaseStream();
		rangeDecoder.ReleaseStream();

		this._outWindow!.Dispose();
		this._outWindow = null;
	}

	public void Dispose()
	{
		this._outWindow?.Dispose();
		this._outWindow = null;
	}

	public void SetDecoderProperties(byte[] properties)
	{
		this.SetDecoderProperties(properties.AsSpan());
	}
}