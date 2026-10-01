// Portions copyright (c) 2014 Adam Hathcock and the SharpCompress contributors.
// Licensed under the MIT License.

using System;
using System.Runtime.CompilerServices;
using VCDiff.Compression.LZMA.LZ;
using VCDiff.Compression.LZMA.RangeCoder;

namespace VCDiff.Compression.LZMA;

// Fast LZMA decode path. This mirrors the design of the
// reference LZMA SDK / 7-Zip C decoder (LzmaDec.c): probabilities are stored in flat ushort
// arrays (instead of arrays of bit-model structs reached through nested decoder objects),
// range/code/dictionary-position/input-buffer-position are kept in local variables (pinned
// with `fixed` and accessed through raw pointers, exactly like the C reference's `probs`/
// `dic`/`buf` locals) for the duration of the decode loop, instead of being re-read from
// object fields - and re-bounds-checked - on every bit. Compressed input is consumed from a
// buffered reader (RangeDecoder's fast buffer) instead of one virtual Stream.ReadByte()
// call per byte.
internal sealed partial class LzmaDecoder
{
	// Mutable range-coder state threaded through the decode helpers below as a single `ref`
	// parameter (instead of one `ref` per field), pinned to the RangeDecoder's fast
	// input buffer for the duration of one CodeFast call. Consumed byte count is batched into
	// RangeDecoder._total only when the local buffer is refilled/at call exit, instead
	// of touching that field on every single byte.
	private unsafe struct FastRangeState
	{
		public uint Range;
		public uint Code;
		public byte* InBuf;
		public int InPos;
		public int InLen;
		public long Consumed;
		public RangeDecoder RangeDecoder;

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public byte ReadByte()
		{
			if (this.InPos >= this.InLen)
			{
				this.RangeDecoder.FastBufferPos = this.InPos;
				this.RangeDecoder.AddTotal(this.Consumed);
				this.Consumed = 0;
				this.RangeDecoder.RefillFast();
				this.InPos = this.RangeDecoder.FastBufferPos;
				this.InLen = this.RangeDecoder.FastBufferLen;
			}

			this.Consumed++;
			return this.InBuf[this.InPos++];
		}
	}

	// Mutable dictionary/output-window state, pinned to the OutWindow's circular buffer for
	// the duration of one CodeFast call. Mirrors dicPos/dic being plain locals in the
	// reference 7-Zip C decoder instead of being re-read from the OutWindow object.
	private unsafe struct FastOutState
	{
		public byte* Dic;
		public int Pos;
		public long Total;
		public int WindowSize;
		public long Limit;
		public OutWindow OutWindow;

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public readonly byte GetByte(int distance)
		{
			var p = this.Pos - distance - 1;
			if (p < 0) p += this.WindowSize;
			return this.Dic[p];
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public void PutByte(byte value)
		{
			this.Dic[this.Pos++] = value;
			this.Total++;

			// Reaching the window end stops the decode loop; OutWindow.Read wraps the position
			// once the caller has drained the window.
		}

		public void CopyBlock(int distance, int len)
		{
			var rem = len;
			var p = (distance < this.Pos ? this.Pos : this.Pos + this.WindowSize) - distance - 1;
			var targetSize =
				this.Pos < this.WindowSize && this.Total < this.Limit ? (int)Math.Min(rem, this.Limit - this.Total) : 0;
			var sizeUntilWindowEnd = Math.Min(this.WindowSize - this.Pos, this.WindowSize - p);
			var sizeUntilOverlap = Math.Abs(p - this.Pos);
			var fastSize = Math.Min(Math.Min(sizeUntilWindowEnd, sizeUntilOverlap), targetSize);
			if (fastSize >= 2)
			{
				Buffer.MemoryCopy(this.Dic + p, this.Dic + this.Pos, fastSize, fastSize);
				this.Pos += fastSize;
				p += fastSize;
				this.Total += fastSize;
				rem -= fastSize;
			}

			while (rem > 0 && this.Pos < this.WindowSize && this.Total < this.Limit)
			{
				if (p >= this.WindowSize) p = 0;

				this.PutByte(this.Dic[p++]);
				rem--;
			}

			this.OutWindow.SetPendingFast(distance, rem);
		}
	}

	// Bias used by the branchless probability-update formula in DecodeBitFast (see there for
	// the derivation): (1 << KNumMoveBitsFast) - 1.
	private const int K_BIT_MODEL_OFFSET_FAST = (1 << K_NUM_MOVE_BITS_FAST) - 1;
	private const int K_NUM_MOVE_BITS_FAST = 5;
	private const int LEN_CHOICE2_INDEX = 1;

	// Choice/Choice2 flags followed by Low (16 posStates * 8 symbols), Mid (16 posStates * 8
	// symbols) and High (256 symbols) trees, laid out contiguously rather than reusing 7-Zip's
	// overlapping ASM-oriented addressing, to keep the arithmetic straightforward and safe.
	private const int LEN_CHOICE_INDEX = 0;
	private const int LEN_HIGH_BASE = LEN_MID_BASE + LEN_MID_SIZE;
	private const int LEN_HIGH_SIZE = 1 << LzmaBase.K_NUM_HIGH_LEN_BITS; // 256
	private const int LEN_LOW_BASE = 2;
	private const int LEN_LOW_SIZE = (int)LzmaBase.K_NUM_POS_STATES_MAX * LEN_LOW_STRIDE; // 128
	private const int LEN_LOW_STRIDE = 1 << LzmaBase.K_NUM_LOW_LEN_BITS; // 8
	private const int LEN_MID_BASE = LEN_LOW_BASE + LEN_LOW_SIZE;
	private const int LEN_MID_SIZE = (int)LzmaBase.K_NUM_POS_STATES_MAX * LEN_MID_STRIDE; // 128
	private const int LEN_MID_STRIDE = 1 << LzmaBase.K_NUM_MID_LEN_BITS; // 8
	private const int LEN_PROBS_SIZE = LEN_HIGH_BASE + LEN_HIGH_SIZE;

	private ushort[] _fIsMatch = null!;
	private ushort[] _fIsRep = null!;
	private ushort[] _fIsRep0Long = null!;
	private ushort[] _fIsRepG0 = null!;
	private ushort[] _fIsRepG1 = null!;
	private ushort[] _fIsRepG2 = null!;
	private ushort[] _fLenProbs = null!;
	private ushort[] _fLiteral = null!;
	private int _fLiteralNumPosBits = -1;

	private int _fLiteralNumPrevBits = -1;
	private uint _fLiteralPosMask;
	private ushort[] _fPosAlign = null!;
	private ushort[] _fPosDecoders = null!;
	private ushort[] _fPosSlot = null!;
	private ushort[] _fRepLenProbs = null!;

	private void CreateFastModel(int lp, int lc)
	{
		this._fIsMatch ??= new ushort[LzmaBase.K_NUM_STATES << LzmaBase.K_NUM_POS_STATES_BITS_MAX];
		this._fIsRep ??= new ushort[LzmaBase.K_NUM_STATES];
		this._fIsRepG0 ??= new ushort[LzmaBase.K_NUM_STATES];
		this._fIsRepG1 ??= new ushort[LzmaBase.K_NUM_STATES];
		this._fIsRepG2 ??= new ushort[LzmaBase.K_NUM_STATES];
		this._fIsRep0Long ??= new ushort[LzmaBase.K_NUM_STATES << LzmaBase.K_NUM_POS_STATES_BITS_MAX];
		this._fPosSlot ??= new ushort[LzmaBase.K_NUM_LEN_TO_POS_STATES << LzmaBase.K_NUM_POS_SLOT_BITS];
		this._fPosDecoders ??= new ushort[LzmaBase.K_NUM_FULL_DISTANCES - LzmaBase.K_END_POS_MODEL_INDEX];
		this._fPosAlign ??= new ushort[1 << LzmaBase.K_NUM_ALIGN_BITS];
		this._fLenProbs ??= new ushort[LEN_PROBS_SIZE];
		this._fRepLenProbs ??= new ushort[LEN_PROBS_SIZE];

		if (this._fLiteralNumPrevBits != lc || this._fLiteralNumPosBits != lp)
		{
			this._fLiteralNumPrevBits = lc;
			this._fLiteralNumPosBits = lp;
			this._fLiteralPosMask = ((uint)1 << lp) - 1;
			var numStates = (uint)1 << (lc + lp);
			this._fLiteral = new ushort[checked((int)(numStates * 0x300))];
		}
	}

	private void InitFastModel()
	{
		const ushort PROB_INIT = (ushort)(RangeDecoder.K_BIT_MODEL_TOTAL >> 1);
		Array.Fill(this._fIsMatch, PROB_INIT);
		Array.Fill(this._fIsRep, PROB_INIT);
		Array.Fill(this._fIsRepG0, PROB_INIT);
		Array.Fill(this._fIsRepG1, PROB_INIT);
		Array.Fill(this._fIsRepG2, PROB_INIT);
		Array.Fill(this._fIsRep0Long, PROB_INIT);
		Array.Fill(this._fPosSlot, PROB_INIT);
		Array.Fill(this._fPosDecoders, PROB_INIT);
		Array.Fill(this._fPosAlign, PROB_INIT);
		Array.Fill(this._fLenProbs, PROB_INIT);
		Array.Fill(this._fRepLenProbs, PROB_INIT);
		Array.Fill(this._fLiteral, PROB_INIT);
	}

	internal unsafe bool CodeFast
	(
		int dictionarySize,
		OutWindow outWindow,
		RangeDecoder rangeDecoder
	)
	{
		var dictionarySizeCheck = Math.Max(dictionarySize, 1);

		outWindow.CopyPending();

		var dic = outWindow.FastBuffer;
		var inBuf = rangeDecoder.FastBufferArray;
		bool result;

		fixed (byte* pDic = dic)
		fixed (byte* pIn = inBuf)
		fixed (
			ushort* pIsMatch = this._fIsMatch,
			pIsRep = this._fIsRep,
			pIsRepG0 = this._fIsRepG0,
			pIsRepG1 = this._fIsRepG1,
			pIsRepG2 = this._fIsRepG2,
			pIsRep0Long = this._fIsRep0Long,
			pPosSlot = this._fPosSlot,
			pPosDecoders = this._fPosDecoders,
			pPosAlign = this._fPosAlign,
			pLenProbs = this._fLenProbs,
			pRepLenProbs = this._fRepLenProbs,
			pLiteral = this._fLiteral
		)
		{
			var rs = new FastRangeState {
				Range = rangeDecoder.Range,
				Code = rangeDecoder.Code,
				InBuf = pIn,
				InPos = rangeDecoder.FastBufferPos,
				InLen = rangeDecoder.FastBufferLen,
				Consumed = 0,
				RangeDecoder = rangeDecoder
			};
			var os = new FastOutState {
				Dic = pDic,
				Pos = outWindow.FastPos,
				Total = outWindow.FastTotal,
				WindowSize = outWindow.FastWindowSize,
				Limit = outWindow.FastLimit,
				OutWindow = outWindow
			};

			result = false;
			while (os.Pos < os.WindowSize && os.Total < os.Limit)
			{
				var posState = (uint)os.Total & this._posStateMask;
				var stateIndex = (int)this._state.Index;

				// (stateIndex << K_NUM_POS_STATES_BITS_MAX) + posState is used both as the
				// IsMatch index and (numerically identical) the IsRep0Long/"short rep" index.
				var matchIndex = (int)((stateIndex << LzmaBase.K_NUM_POS_STATES_BITS_MAX) + posState);

				// prevByte/matchByte only depend on os.Pos/_rep0, both already fixed from the
				// previous iteration - independent of the upcoming (unavoidably branchy, since
				// it changes the whole loop-body shape) IsMatch dispatch below. Issuing these
				// dictionary reads before that branch resolves lets their load latency overlap
				// with the IsMatch bit-decode arithmetic instead of stalling right after the
				// branch, where they'd otherwise sit on the critical path. (Tried hoisting the
				// literal-tree base-index/root-probability computation the same way too, but it
				// regressed ~1s - that computation is heavy enough that doing it unconditionally
				// on every iteration, including non-literal ones, outweighs the latency-hiding
				// benefit; kept lazy, computed only once IsMatch resolves to 0.)
				var isCharState = this._state.IsCharState();
				var prevByte = os.GetByte(0);
				var matchByte = isCharState ? (byte)0 : os.GetByte((int)this._rep0);

				// Split compare/update (mirrors ASM's IF_BIT_x_NOUP + deferred UPDATE_0/
				// UPDATE_1): the probability write only happens once we know which arm we're
				// in, interleaved with that arm's own independent setup work below, instead of
				// being on the decode's own critical path.
				var probIsMatch = pIsMatch[matchIndex];
				var isMatchSymbol = DecodeBitFastNoUpdate(ref rs, probIsMatch, out var isMatchMask);

				if (isMatchSymbol == 0)
				{
					UpdateProbFast(pIsMatch + matchIndex, probIsMatch, isMatchMask);
					var literalP =
						pLiteral + (int)this.GetFastLiteralBaseIndex((uint)os.Total, prevByte);
					var firstIndex = isCharState
						? 1u
						: (uint)((((matchByte >> 7) & 1) + 1) << 8) + 1;
					var firstProb = (uint)literalP[firstIndex];
					var b = isCharState
						? LiteralDecodeNormalFast(ref rs, literalP, firstProb)
						: LiteralDecodeWithMatchByteFast(
							ref rs,
							literalP,
							matchByte,
							firstIndex,
							firstProb
						);
					os.PutByte(b);
					this._state.UpdateChar();
					continue;
				}

				UpdateProbFast(pIsMatch + matchIndex, probIsMatch, isMatchMask);

				uint len;
				if (DecodeBitFast(ref rs, pIsRep, stateIndex) == 1)
				{
					if (DecodeBitFast(ref rs, pIsRepG0, stateIndex) == 0)
					{
						if (DecodeBitFast(ref rs, pIsRep0Long, matchIndex) == 0)
						{
							// A short rep copies the byte at rep0; with an empty window there is none.
							if (os.Total == 0) throw new InvalidFormatException("LZMA data error");

							this._state.UpdateShortRep();
							os.PutByte(os.GetByte((int)this._rep0));
							continue;
						}
					}
					else
					{
						uint distance;
						if (DecodeBitFast(ref rs, pIsRepG1, stateIndex) == 0)
							distance = this._rep1;
						else
						{
							if (DecodeBitFast(ref rs, pIsRepG2, stateIndex) == 0)
								distance = this._rep2;
							else
							{
								distance = this._rep3;
								this._rep3 = this._rep2;
							}

							this._rep2 = this._rep1;
						}

						this._rep1 = this._rep0;
						this._rep0 = distance;
					}

					len = LenDecodeFast(ref rs, pRepLenProbs, posState) + LzmaBase.K_MATCH_MIN_LEN;
					this._state.UpdateRep();
				}
				else
				{
					this._rep3 = this._rep2;
					this._rep2 = this._rep1;
					this._rep1 = this._rep0;
					len = LzmaBase.K_MATCH_MIN_LEN + LenDecodeFast(ref rs, pLenProbs, posState);
					this._state.UpdateMatch();
					var posSlot = BitTreeDecodeFast(
						ref rs,
						pPosSlot,
						(int)(LzmaBase.GetLenToPosState(len) << LzmaBase.K_NUM_POS_SLOT_BITS),
						LzmaBase.K_NUM_POS_SLOT_BITS
					);
					if (posSlot >= LzmaBase.K_START_POS_MODEL_INDEX)
					{
						var numDirectBits = (int)((posSlot >> 1) - 1);
						this._rep0 = (2 | (posSlot & 1)) << numDirectBits;
						if (posSlot < LzmaBase.K_END_POS_MODEL_INDEX)
						{
							this._rep0 += BitTreeReverseDecodeFast(
								ref rs,
								pPosDecoders,
								(int)(this._rep0 - posSlot - 1),
								numDirectBits
							);
						}
						else
						{
							this._rep0 +=
								DecodeDirectBitsFast(ref rs, numDirectBits - LzmaBase.K_NUM_ALIGN_BITS) << LzmaBase.K_NUM_ALIGN_BITS;
							this._rep0 += BitTreeReverseDecodeFast(
								ref rs,
								pPosAlign,
								0,
								LzmaBase.K_NUM_ALIGN_BITS
							);
						}
					}
					else
						this._rep0 = posSlot;
				}

				if (this._rep0 >= os.Total || this._rep0 >= dictionarySizeCheck)
				{
					if (this._rep0 == 0xFFFFFFFF)
					{
						result = true;
						break;
					}

					rangeDecoder.Range = rs.Range;
					rangeDecoder.Code = rs.Code;
					rangeDecoder.FastBufferPos = rs.InPos;
					rangeDecoder.AddTotal(rs.Consumed);
					outWindow.FastPos = os.Pos;
					outWindow.FastTotal = os.Total;
					throw new InvalidFormatException("LZMA data error");
				}

				os.CopyBlock((int)this._rep0, (int)len);
			}

			rangeDecoder.Range = rs.Range;
			rangeDecoder.Code = rs.Code;
			rangeDecoder.FastBufferPos = rs.InPos;
			rangeDecoder.AddTotal(rs.Consumed);
			outWindow.FastPos = os.Pos;
			outWindow.FastTotal = os.Total;
		}

		return result;
	}

	// Core of DecodeBitFast, factored out so the bit-tree/literal decoders below can pass in a
	// probability that was already loaded (prefetched) by the *previous* tree level instead of
	// loading it here, and can read back `mask` to select their own prefetched next-level
	// probability without an extra branch. `probSlot` must point at the array slot the caller
	// read `prob` from, for the write-back.
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private static unsafe uint DecodeBitFastCore
	(
		ref FastRangeState rs,
		ushort* probSlot,
		uint prob,
		out uint mask
	)
	{
		var range = rs.Range;
		var bound = (range >> RangeDecoder.K_NUM_BIT_MODEL_TOTAL_BITS) * prob;

		// Branchless bit decode + probability update, mirroring 7-Zip's ASM decoder
		// (Asm/x86/LzmaDecOpt.asm) rather than the reference C decoder's data-dependent
		// `if`: both possible next-states are computed unconditionally and combined via an
		// all-ones/all-zeros mask (the software equivalent of the ASM's cmovae/cmovb),
		// instead of branching on a comparison whose outcome is close to 50/50 for
		// well-compressed data and is therefore poorly predicted by the CPU.
		var symbol = rs.Code < bound ? 0u : 1u;
		mask = (uint)-(int)symbol; // 0 for symbol 0, 0xFFFFFFFF for symbol 1

		rs.Range = (bound & ~mask) | ((range - bound) & mask);
		rs.Code -= bound & mask;

		// UPDATE_0 (symbol 0) is `prob + ((K_BIT_MODEL_TOTAL - prob) >> 5)`; UPDATE_1
		// (symbol 1) is `prob - (prob >> 5)`. KBitModelOffsetFast (31) is the standard LZMA
		// bias that makes a single *arithmetic* (signed) shift reproduce UPDATE_1's
		// logical-shift result from a target of 0, so both formulas collapse into one
		// branchless expression selected by the same mask used above.
		var target = (int)(
			(RangeDecoder.K_BIT_MODEL_TOTAL & ~mask) | K_BIT_MODEL_OFFSET_FAST & mask
		);
		*probSlot = (ushort)((int)prob + ((target - (int)prob) >> K_NUM_MOVE_BITS_FAST));

		if (rs.Range < RangeDecoder.K_TOP_VALUE)
		{
			rs.Range <<= 8;
			rs.Code = (rs.Code << 8) | rs.ReadByte();
		}

		return symbol;
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private static unsafe uint DecodeBitFast(ref FastRangeState rs, ushort* probs, int index)
	{
		return DecodeBitFastCore(ref rs, probs + index, probs[index], out _);
	}

	// Split form of DecodeBitFastCore used only for the outer dispatch bit (IsMatch): mirrors
	// the ASM's IF_BIT_x_NOUP / UPDATE_0 / UPDATE_1 split, where the probability-model update is
	// deferred past the (unavoidable, since literal vs. match are wholly different code) branch
	// so it can be interleaved with independent work in each arm instead of sitting on the
	// decode's own critical path.
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private static uint DecodeBitFastNoUpdate(ref FastRangeState rs, uint prob, out uint mask)
	{
		var range = rs.Range;
		var bound = (range >> RangeDecoder.K_NUM_BIT_MODEL_TOTAL_BITS) * prob;
		var symbol = rs.Code < bound ? 0u : 1u;
		mask = (uint)-(int)symbol;
		rs.Range = (bound & ~mask) | ((range - bound) & mask);
		rs.Code -= bound & mask;
		if (rs.Range < RangeDecoder.K_TOP_VALUE)
		{
			rs.Range <<= 8;
			rs.Code = (rs.Code << 8) | rs.ReadByte();
		}

		return symbol;
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private static unsafe void UpdateProbFast(ushort* probSlot, uint prob, uint mask)
	{
		var target = (int)(
			(RangeDecoder.K_BIT_MODEL_TOTAL & ~mask) | K_BIT_MODEL_OFFSET_FAST & mask
		);
		*probSlot = (ushort)((int)prob + ((target - (int)prob) >> K_NUM_MOVE_BITS_FAST));
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private static unsafe uint BitTreeDecodeFast
	(
		ref FastRangeState rs,
		ushort* probs,
		int baseIndex,
		int numBits
	)
	{
		var p = probs + baseIndex;
		uint m = 1;

		// The very first probability must still be loaded fresh; from then on `prob` always
		// arrives pre-loaded from the previous level's prefetch below.
		var prob = (uint)p[1];
		for (var i = 0; i < numBits; i++)
		{
			var child0 = m << 1;

			// Prefetch BOTH possible next-level probabilities now: which child we'll actually
			// need depends on the bit we're about to decode, but the child *indices* only
			// depend on `m`, which is already known - so these loads can proceed in parallel
			// with (instead of only after) the branchless decode below, hiding their latency.
			var hasNext = i + 1 < numBits;
			uint probChild0 = 0,
				probChild1 = 0;
			if (hasNext)
			{
				probChild0 = p[child0];
				probChild1 = p[child0 + 1];
			}

			var bit = DecodeBitFastCore(ref rs, p + m, prob, out var mask);

			m = child0 + bit;
			if (hasNext) prob = (probChild0 & ~mask) | (probChild1 & mask);
		}

		return m - ((uint)1 << numBits);
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private static unsafe uint BitTreeReverseDecodeFast
	(
		ref FastRangeState rs,
		ushort* probs,
		int baseIndex,
		int numBits
	)
	{
		var p = probs + baseIndex;
		uint m = 1;
		var prob = (uint)p[1];
		uint symbol = 0;
		for (var i = 0; i < numBits; i++)
		{
			var child0 = m << 1;
			var hasNext = i + 1 < numBits;
			uint probChild0 = 0,
				probChild1 = 0;
			if (hasNext)
			{
				probChild0 = p[child0];
				probChild1 = p[child0 + 1];
			}

			var bit = DecodeBitFastCore(ref rs, p + m, prob, out var mask);
			symbol |= bit << i;

			m = child0 + bit;
			if (hasNext) prob = (probChild0 & ~mask) | (probChild1 & mask);
		}

		return symbol;
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private static uint DecodeDirectBitsFast(ref FastRangeState rs, int numTotalBits)
	{
		uint result = 0;
		for (var i = numTotalBits; i > 0; i--)
		{
			rs.Range >>= 1;
			var t = (rs.Code - rs.Range) >> 31;
			rs.Code -= rs.Range & (t - 1);
			result = (result << 1) | (1 - t);
			if (rs.Range < RangeDecoder.K_TOP_VALUE)
			{
				rs.Code = (rs.Code << 8) | rs.ReadByte();
				rs.Range <<= 8;
			}
		}

		return result;
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private static unsafe uint LenDecodeFast(ref FastRangeState rs, ushort* probs, uint posState)
	{
		if (DecodeBitFast(ref rs, probs, LEN_CHOICE_INDEX) == 0)
		{
			return BitTreeDecodeFast(
				ref rs,
				probs,
				LEN_LOW_BASE + (int)posState * LEN_LOW_STRIDE,
				LzmaBase.K_NUM_LOW_LEN_BITS
			);
		}

		var symbol = LzmaBase.K_NUM_LOW_LEN_SYMBOLS;
		if (DecodeBitFast(ref rs, probs, LEN_CHOICE2_INDEX) == 0)
		{
			symbol += BitTreeDecodeFast(
				ref rs,
				probs,
				LEN_MID_BASE + (int)posState * LEN_MID_STRIDE,
				LzmaBase.K_NUM_MID_LEN_BITS
			);
		}
		else
		{
			symbol += LzmaBase.K_NUM_MID_LEN_SYMBOLS;
			symbol += BitTreeDecodeFast(ref rs, probs, LEN_HIGH_BASE, LzmaBase.K_NUM_HIGH_LEN_BITS);
		}

		return symbol;
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private uint GetFastLiteralBaseIndex(uint pos, byte prevByte)
	{
		return (
				((pos & this._fLiteralPosMask) << this._fLiteralNumPrevBits) + (uint)(prevByte >> (8 - this._fLiteralNumPrevBits))
			) *
			0x300;
	}

	// baseP/firstProb (root of the literal tree, index 1) are precomputed by the caller -
	// pos/prevByte are already known before the IsMatch decode resolves, so hoisting this load
	// out lets it overlap with the IsMatch bit-decode instead of stalling right after it.
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private static unsafe byte LiteralDecodeNormalFast
	(
		ref FastRangeState rs,
		ushort* baseP,
		uint firstProb
	)
	{
		var p = baseP;

		// Same one-level-ahead child-probability prefetch as BitTreeDecodeFast (see there),
		// applied to the hottest call site in the decoder: one 8-bit literal-tree decode per
		// literal byte. Manually unrolled (mirroring the ASM's explicit BIT_0/BIT_1x7
		// sequence, as opposed to a loop) because RyuJIT does not inline methods containing
		// a loop - leaving this as a `for` loop, even with AggressiveInlining, forces this
		// call to remain a real call boundary, which makes `rs` address-exposed and prevents
		// the JIT from keeping Range/Code in registers for the rest of the CodeFast loop.
		uint m = 1;
		var prob = firstProb;

		var child0 = m << 1;
		var probChild0 = p[child0];
		var probChild1 = p[child0 + 1];
		var bit = DecodeBitFastCore(ref rs, p + m, prob, out var mask);
		m = child0 + bit;
		prob = (probChild0 & ~mask) | (probChild1 & mask);

		child0 = m << 1;
		probChild0 = p[child0];
		probChild1 = p[child0 + 1];
		bit = DecodeBitFastCore(ref rs, p + m, prob, out mask);
		m = child0 + bit;
		prob = (probChild0 & ~mask) | (probChild1 & mask);

		child0 = m << 1;
		probChild0 = p[child0];
		probChild1 = p[child0 + 1];
		bit = DecodeBitFastCore(ref rs, p + m, prob, out mask);
		m = child0 + bit;
		prob = (probChild0 & ~mask) | (probChild1 & mask);

		child0 = m << 1;
		probChild0 = p[child0];
		probChild1 = p[child0 + 1];
		bit = DecodeBitFastCore(ref rs, p + m, prob, out mask);
		m = child0 + bit;
		prob = (probChild0 & ~mask) | (probChild1 & mask);

		child0 = m << 1;
		probChild0 = p[child0];
		probChild1 = p[child0 + 1];
		bit = DecodeBitFastCore(ref rs, p + m, prob, out mask);
		m = child0 + bit;
		prob = (probChild0 & ~mask) | (probChild1 & mask);

		child0 = m << 1;
		probChild0 = p[child0];
		probChild1 = p[child0 + 1];
		bit = DecodeBitFastCore(ref rs, p + m, prob, out mask);
		m = child0 + bit;
		prob = (probChild0 & ~mask) | (probChild1 & mask);

		child0 = m << 1;
		probChild0 = p[child0];
		probChild1 = p[child0 + 1];
		bit = DecodeBitFastCore(ref rs, p + m, prob, out mask);
		m = child0 + bit;
		prob = (probChild0 & ~mask) | (probChild1 & mask);

		child0 = m << 1;
		bit = DecodeBitFastCore(ref rs, p + m, prob, out mask);
		m = child0 + bit;

		return (byte)m;
	}

	// Shared plain-literal-tree tail used once a matched-literal decode diverges from
	// matchByte: from that point on there's no more matched-zone offset, so this is the same
	// decode as LiteralDecodeNormalFast's inner step, just resuming mid-tree from `symbol`.
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private static unsafe byte LiteralDecodeTailFast(ref FastRangeState rs, ushort* p, uint symbol)
	{
		while (symbol < 0x100)
		{
			var bit = DecodeBitFastCore(ref rs, p + symbol, p[symbol], out _);
			symbol = (symbol << 1) | bit;
		}

		return (byte)symbol;
	}

	// baseP/firstIndex/firstProb are precomputed by the caller for the same reason as in
	// LiteralDecodeNormalFast above (pos/prevByte/matchByte are all already known before the
	// IsMatch decode resolves).
	private static unsafe byte LiteralDecodeWithMatchByteFast
	(
		ref FastRangeState rs,
		ushort* baseP,
		byte matchByte,
		uint firstIndex,
		uint firstProb
	)
	{
		var p = baseP;

		// Same one-level-ahead prefetch as BitTreeDecodeFast, adapted for the matched-literal
		// zone: matchByte's bits are all known upfront (it comes from the dictionary, not from
		// decoding), so the *next* iteration's matched-zone index can be computed from it
		// immediately, independent of this iteration's decoded bit.
		uint symbol = 1;
		var matchBit = (uint)(matchByte >> 7) & 1;
		matchByte <<= 1;
		var index = (int)firstIndex;
		var prob = firstProb;

		for (var i = 0; i < 8; i++)
		{
			var nextMatchBit = (uint)(matchByte >> 7) & 1;
			matchByte <<= 1;
			var hasNext = i < 7;

			var child0 = ((1 + nextMatchBit) << 8) + (symbol << 1);
			uint probChild0 = 0,
				probChild1 = 0;
			if (hasNext)
			{
				probChild0 = p[child0];
				probChild1 = p[child0 + 1];
			}

			var bit = DecodeBitFastCore(ref rs, p + index, prob, out var mask);
			symbol = (symbol << 1) | bit;

			if (matchBit != bit) return LiteralDecodeTailFast(ref rs, p, symbol);

			matchBit = nextMatchBit;
			if (hasNext)
			{
				index = (int)(child0 + bit);
				prob = (probChild0 & ~mask) | (probChild1 & mask);
			}
		}

		return (byte)symbol;
	}
}