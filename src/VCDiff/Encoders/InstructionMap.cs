// Copyright (c) Metric and the Snowflake Authors.
// Licensed under the Apache License, Version 2.0.

using System;
using VCDiff.Includes;
using VCDiff.Shared;

namespace VCDiff.Encoders;

internal class InstructionMap
{
	private readonly struct OpcodeMap2
	{
		private readonly int[][][] opcodes2;
		private readonly int maxSize;
		private readonly int numInstAndModes;

		public OpcodeMap2(int numInstAndModes, int maxSize)
		{
			this.maxSize = maxSize;
			this.numInstAndModes = numInstAndModes;
			this.opcodes2 = new int[CodeTable.KCodeTableSize][][];
		}

		public void Add(byte first, byte inst, byte size, byte mode, byte opcode)
		{
			var instmode = this.opcodes2[first];

			if (instmode == null)
			{
				instmode = new int[this.numInstAndModes][];
				this.opcodes2[opcode] = instmode;
			}

			var sizeArray = instmode[inst + mode];
			if (sizeArray == null)
			{
				sizeArray = this.NewSizeOpcodeArray(this.maxSize + 1);
				instmode[inst + mode] = sizeArray;
			}

			if (sizeArray[size] == CodeTable.KNoOpcode) sizeArray[size] = opcode;
		}

		private int[] NewSizeOpcodeArray(int size)
		{
			var nn = new int[size];
			new Span<int>(nn).Fill(CodeTable.KNoOpcode);
			return nn;
		}

		public int LookUp(byte first, byte inst, byte size, byte mode)
		{
			if (size > this.maxSize) return CodeTable.KNoOpcode;

			var instmode = this.opcodes2[first];
			if (instmode == null) return CodeTable.KNoOpcode;

			var instModePointer = inst == CodeTable.C ? inst + mode : inst;
			return instmode[instModePointer]?[size] ?? CodeTable.KNoOpcode;
		}
	}

	private readonly struct OpcodeMap
	{
		private readonly int[] opcodes;
		private readonly int maxSize;
		private readonly int numInstAndModes;

		public OpcodeMap(int numInstAndModes, int maxSize)
		{
			this.maxSize = maxSize + 1;
			this.numInstAndModes = numInstAndModes;
			this.opcodes = new int[numInstAndModes * this.maxSize];
			new Span<int>(this.opcodes).Fill(CodeTable.KNoOpcode);
		}

		public void Add(byte inst, byte size, byte mode, byte opcode)
		{
			if (this.opcodes[inst + mode + this.numInstAndModes * size] == CodeTable.KNoOpcode) this.opcodes[inst + mode + this.numInstAndModes * size] = opcode;
		}

		public int LookUp(byte inst, byte size, byte mode)
		{
			var instMode = inst == CodeTable.C ? inst + mode : inst;

			if (size > this.maxSize - 1) return CodeTable.KNoOpcode;

			return this.opcodes[instMode + this.numInstAndModes * size];
		}
	}

	public static InstructionMap Instance = new();

	private readonly CodeTable table;
	private OpcodeMap firstMap;
	private OpcodeMap2 secondMap;

    /// <summary>
    ///     Instruction mapping for op codes and such for using in encoding
    /// </summary>
    public unsafe InstructionMap()
	{
		this.table = CodeTable.DefaultTable;
		var inst2 = this.table.Inst2;
		var inst1 = this.table.Inst1;
		var size2 = this.table.Size2;
		var size1 = this.table.Size1;
		var mode1 = this.table.Mode1;
		var mode2 = this.table.Mode2;

		// max sizes are known for the default code table (18 and 6 respectively).
		this.firstMap = new OpcodeMap((int)VcDiffInstructionType.LAST + AddressCache.DefaultLast + 1, FindMaxSize(size1.AsSpan(), 18));
		this.secondMap = new OpcodeMap2((int)VcDiffInstructionType.LAST + AddressCache.DefaultLast + 1, FindMaxSize(size2.AsSpan(), 6));

		for (var opcode = 0; opcode < CodeTable.KCodeTableSize; ++opcode)
		{
			if (inst2.Pointer[opcode] == CodeTable.N)
				this.firstMap.Add(inst1.Pointer[opcode], size1.Pointer[opcode], mode1.Pointer[opcode], (byte)opcode);
			else if (inst1.Pointer[opcode] == CodeTable.N) this.firstMap.Add(inst1.Pointer[opcode], size1.Pointer[opcode], mode1.Pointer[opcode], (byte)opcode);
		}

		for (var opcode = 0; opcode < CodeTable.KCodeTableSize; ++opcode)
		{
			if (inst1.Pointer[opcode] != CodeTable.N && inst2.Pointer[opcode] != CodeTable.N)
			{
				var found = this.LookFirstOpcode(inst1.Pointer[opcode], size1.Pointer[opcode], mode1.Pointer[opcode]);
				if (found == CodeTable.KNoOpcode) continue;

				this.secondMap.Add((byte)found, inst2.Pointer[opcode], size2.Pointer[opcode], mode2.Pointer[opcode], (byte)opcode);
			}
		}
	}

	public int LookFirstOpcode(byte inst, byte size, byte mode)
	{
		return this.firstMap.LookUp(inst, size, mode);
	}

	public int LookSecondOpcode(byte first, byte inst, byte size, byte mode)
	{
		return this.secondMap.LookUp(first, inst, size, mode);
	}

	private static byte FindMaxSize(ReadOnlySpan<byte> sizes, sbyte knownMaxSize = -1)
	{
		if (knownMaxSize > -1) return (byte)knownMaxSize;

		var maxSize = sizes[0];
		var len = sizes.Length;
		for (var i = 1; i < len; i++)
			if (maxSize < sizes[i])
				maxSize = sizes[i];
		return maxSize;
	}
}