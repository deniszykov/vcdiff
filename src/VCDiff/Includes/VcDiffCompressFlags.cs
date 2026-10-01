// Copyright (c) Metric and the Snowflake Authors.
// Licensed under the Apache License, Version 2.0.

namespace VCDiff.Includes;

// The possible values for the Delta_Indicator field, as described
// in section 4.3 of the RFC:
//
//    "Delta_Indicator:
//     This byte is a set of bits, as shown:
//
//      7 6 5 4 3 2 1 0
//     +-+-+-+-+-+-+-+-+
//     | | | | | | | | |
//     +-+-+-+-+-+-+-+-+
//                ^ ^ ^
//                | | |
//                | | +-- VCD_DATACOMP
//                | +---- VCD_INSTCOMP
//                +------ VCD_ADDRCOMP
//
//          VCD_DATACOMP:   bit value 1.
//          VCD_INSTCOMP:   bit value 2.
//          VCD_ADDRCOMP:   bit value 4.
//
//     [...] If the bit VCD_DECOMPRESS (Section 4.1) was on, each of these
//     sections may have been compressed using the specified secondary
//     compressor.  The bit positions 0 (VCD_DATACOMP), 1
//     (VCD_INSTCOMP), and 2 (VCD_ADDRCOMP) respectively indicate, if
//     non-zero, that the corresponding parts are compressed."
internal enum VcDiffCompressFlags
{
	VCDDATACOMP = 0x01,
	VCDINSTCOMP = 0x02,
	VCDADDRCOMP = 0x04
}
