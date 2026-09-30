// Copyright (c) Metric and the Snowflake Authors.
// Licensed under the Apache License, Version 2.0.

using VCDiff.Includes;
using VCDiff.Shared;

namespace VCDiff.Decoders;

internal class InstructionDecoder
{
	private readonly ByteBuffer source;
	private readonly CodeTable table;
	private int pendingSecond;

    /// <summary>
    ///     Decodes the incoming instruction from the buffer
    /// </summary>
    /// <param name="sin">the instruction buffer</param>
    /// <param name="customTable">custom code table if any. Default is null.</param>
    public InstructionDecoder(ByteBuffer sin, CustomCodeTableDecoder? customTable = null)
	{
		this.table = customTable?.CustomTable ?? CodeTable.DefaultTable;
		this.source = sin;
		this.pendingSecond = CodeTable.KNoOpcode;
	}

    /// <summary>
    ///     Gets the next instruction from the buffer
    /// </summary>
    /// <param name="size">the size</param>
    /// <param name="mode">the mode</param>
    /// <returns></returns>
    public unsafe VcDiffInstructionType Next(out int size, out byte mode)
	{
		byte opcode = 0;
		var instructionType = CodeTable.N;
		var instructionSize = 0;
		byte instructionMode = 0;
		var start = this.source.Position;
		do
		{
			if (this.pendingSecond != CodeTable.KNoOpcode)
			{
				opcode = (byte)this.pendingSecond;
				this.pendingSecond = CodeTable.KNoOpcode;
				instructionType = this.table.Inst2.Pointer[opcode];
				instructionSize = this.table.Size2.Pointer[opcode];
				instructionMode = this.table.Mode2.Pointer[opcode];
				break;
			}

			if (!this.source.CanRead)
			{
				size = 0;
				mode = 0;
				return VcDiffInstructionType.EOD;
			}

			opcode = this.source.PeekByte();
			if (this.table.Inst2.Pointer[opcode] != CodeTable.N) this.pendingSecond = this.source.PeekByte();

			this.source.Next();
			instructionType = this.table.Inst1.Pointer[opcode];
			instructionSize = this.table.Size1.Pointer[opcode];
			instructionMode = this.table.Mode1.Pointer[opcode];
		} while (instructionType == CodeTable.N);

		if (instructionSize == 0)
		{
			switch (size = VarIntBe.ParseInt32(this.source))
			{
				case (int)VcDiffResult.ERROR:
					mode = 0;
					size = 0;
					return VcDiffInstructionType.ERROR;

				case (int)VcDiffResult.EOD:
					mode = 0;
					size = 0;

					//reset it back before we read the instruction
					//otherwise when parsing interleave we will miss data
					this.source.Position = start;
					return VcDiffInstructionType.EOD;
			}
		}
		else
			size = instructionSize;

		mode = instructionMode;
		return (VcDiffInstructionType)instructionType;
	}
}