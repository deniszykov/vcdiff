// Copyright (c) Metric and the Snowflake Authors.
// Licensed under the Apache License, Version 2.0.

namespace VCDiff.Includes;

// The possible values for the Hdr_Indicator field, as described
// in section 4.1 of the RFC:
//
//    "The Hdr_Indicator byte shows if there is any initialization data
//     required to aid in the reconstruction of data in the Window sections.
//     This byte MAY have non-zero values for either, both, or neither of
//     the two bits VCD_DECOMPRESS and VCD_CODETABLE below:
//
//         7 6 5 4 3 2 1 0
//        +-+-+-+-+-+-+-+-+
//        | | | | | | | | |
//        +-+-+-+-+-+-+-+-+
//                     ^ ^
//                     | |
//                     | +-- VCD_DECOMPRESS
//                     +---- VCD_CODETABLE
//
//     If bit 0 (VCD_DECOMPRESS) is non-zero, this indicates that a
//     secondary compressor may have been used to further compress certain
//     parts of the delta encoding data [...]"
// [Secondary compressors are not supported by open-vcdiff.]
//
//    "If bit 1 (VCD_CODETABLE) is non-zero, this indicates that an
//     application-defined code table is to be used for decoding the delta
//     instructions. [...]"
//
internal enum VcDiffCodeFlags
{
	VCDDECOMPRESS = 0x01,
	VCDCODETABLE = 0x02,
	VCDAPPHEADER = 0x04
}
