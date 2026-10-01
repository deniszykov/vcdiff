// Copyright (c) Metric and the Snowflake Authors.
// Licensed under the Apache License, Version 2.0.

namespace VCDiff.Includes;

// The possible values for the Win_Indicator field, as described
// in section 4.2 of the RFC:
//
//    "Win_Indicator:
//
//     This byte is a set of bits, as shown:
//
//      7 6 5 4 3 2 1 0
//     +-+-+-+-+-+-+-+-+
//     | | | | | | | | |
//     +-+-+-+-+-+-+-+-+
//                  ^ ^
//                  | |
//                  | +-- VCD_SOURCE
//                  +---- VCD_TARGET
//
//     If bit 0 (VCD_SOURCE) is non-zero, this indicates that a
//     segment of data from the "source" file was used as the
//     corresponding source window of data to encode the target
//     window.  The decoder will use this same source data segment to
//     decode the target window.
//
//     If bit 1 (VCD_TARGET) is non-zero, this indicates that a
//     segment of data from the "target" file was used as the
//     corresponding source window of data to encode the target
//     window.  As above, this same source data segment is used to
//     decode the target window.
//
//     The Win_Indicator byte MUST NOT have more than one of the bits
//     set (non-zero).  It MAY have none of these bits set."
//
internal enum VcDiffWindowFlags
{
	VCDSOURCE = 0x01,
	VCDTARGET = 0x02,

	//Google Specific Flag
	VCDCHECKSUM = 0x04
}
