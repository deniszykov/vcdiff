// Copyright (c) Metric and the Snowflake Authors.
// Licensed under the Apache License, Version 2.0.

namespace VCDiff.Includes;

internal enum VcDiffInstructionType
{
	NOOP = 0,
	ADD = 1,
	RUN = 2,
	COPY = 3,
	LAST = 3
}
