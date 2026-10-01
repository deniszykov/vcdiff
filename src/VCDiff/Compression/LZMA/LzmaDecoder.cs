// Portions copyright (c) 2014 Adam Hathcock and the SharpCompress contributors.
// Licensed under the MIT License.

using VCDiff.Compression.LZMA.LZ;
using VCDiff.Compression.LZMA.RangeCoder;

namespace VCDiff.Compression.LZMA;

/// <summary>
///     LZMA decoder state: coder properties, the probability model (see <c>LzmaDecoder.Fast.cs</c>), the state
///     machine and the four rep distances. Reused across LZMA2 chunks; <see cref="SetDecoderProperties" /> resets it.
/// </summary>
internal sealed partial class LzmaDecoder
{
	private uint _posStateMask;
	private uint _rep0,
		_rep1,
		_rep2,
		_rep3;

	private LzmaBase.State _state;
	internal bool HasEndMarker => this._rep0 == uint.MaxValue;

	internal bool Code(int dictionarySize, OutWindow outWindow, RangeDecoder rangeDecoder)
	{
		return this.CodeFast(dictionarySize, outWindow, rangeDecoder);
	}

	/// <summary>
	///     Applies the lc/lp/pb properties byte and resets the whole decoder state (LZMA2 state reset).
	/// </summary>
	internal void SetDecoderProperties(byte properties)
	{
		var lc = properties % 9;
		var remainder = properties / 9;
		var lp = remainder % 5;
		var pb = remainder / 5;
		if (pb > LzmaBase.K_NUM_POS_STATES_BITS_MAX) throw new InvalidFormatException("Invalid LZMA properties");

		this._posStateMask = ((uint)1 << pb) - 1;
		this.CreateFastModel(lp, lc);
		this.InitFastModel();

		this._state.Init();
		this._rep0 = 0;
		this._rep1 = 0;
		this._rep2 = 0;
		this._rep3 = 0;
	}
}
