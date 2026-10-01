// Copyright (c) Metric and the Snowflake Authors.
// Licensed under the Apache License, Version 2.0.

namespace VCDiff.Includes;

// The address modes used for COPY instructions, as defined in
// section 5.3 of the RFC.
//
// The first two modes (0 and 1) are defined as SELF (addressing forward
// from the beginning of the source window) and HERE (addressing backward
// from the current position in the source window + previously decoded
// target data.)
//
// After those first two modes, there are a variable number of NEAR modes
// (which take a recently-used address and add a positive offset to it)
// and SAME modes (which match a previously-used address using a "hash" of
// the lowest bits of the address.)  The number of NEAR and SAME modes
// depends on the defined size of the address cache; since this number is
// variable, these modes cannot be specified as enum values.
internal enum VcDiffModes
{
	SELF = 0,
	HERE = 1,
	FIRST = 2,
	MAX = byte.MaxValue + 1
}
