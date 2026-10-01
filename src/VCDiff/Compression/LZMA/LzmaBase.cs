// Portions copyright (c) 2014 Adam Hathcock and the SharpCompress contributors.
// Licensed under the MIT License.

namespace VCDiff.Compression.LZMA;

/// <summary>
///     LZMA model constants and the literal/match/rep state machine.
/// </summary>
internal static class LzmaBase
{
	public struct State
	{
		public uint Index;

		public void Init()
		{
			this.Index = 0;
		}

		public void UpdateChar()
		{
			if (this.Index < 4)
				this.Index = 0;
			else if (this.Index < 10)
				this.Index -= 3;
			else
				this.Index -= 6;
		}

		public void UpdateMatch()
		{
			this.Index = (uint)(this.Index < 7 ? 7 : 10);
		}

		public void UpdateRep()
		{
			this.Index = (uint)(this.Index < 7 ? 8 : 11);
		}

		public void UpdateShortRep()
		{
			this.Index = (uint)(this.Index < 7 ? 9 : 11);
		}

		public bool IsCharState()
		{
			return this.Index < 7;
		}
	}

	public const uint K_END_POS_MODEL_INDEX = 14;
	public const uint K_MATCH_MIN_LEN = 2;
	public const int K_NUM_ALIGN_BITS = 4;
	public const uint K_NUM_FULL_DISTANCES = 1 << ((int)K_END_POS_MODEL_INDEX / 2);
	public const int K_NUM_HIGH_LEN_BITS = 8;
	public const uint K_NUM_LEN_TO_POS_STATES = 1 << K_NUM_LEN_TO_POS_STATES_BITS;
	private const int K_NUM_LEN_TO_POS_STATES_BITS = 2; // it's for speed optimization
	public const int K_NUM_LOW_LEN_BITS = 3;
	public const uint K_NUM_LOW_LEN_SYMBOLS = 1 << K_NUM_LOW_LEN_BITS;
	public const int K_NUM_MID_LEN_BITS = 3;
	public const uint K_NUM_MID_LEN_SYMBOLS = 1 << K_NUM_MID_LEN_BITS;
	public const int K_NUM_POS_SLOT_BITS = 6;
	public const int K_NUM_POS_STATES_BITS_MAX = 4;
	public const uint K_NUM_POS_STATES_MAX = 1 << K_NUM_POS_STATES_BITS_MAX;
	public const uint K_NUM_STATES = 12;
	public const uint K_START_POS_MODEL_INDEX = 4;

	public static uint GetLenToPosState(uint len)
	{
		len -= K_MATCH_MIN_LEN;
		if (len < K_NUM_LEN_TO_POS_STATES) return len;

		return K_NUM_LEN_TO_POS_STATES - 1;
	}
}
