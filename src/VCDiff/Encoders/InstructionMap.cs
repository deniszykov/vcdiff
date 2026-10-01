// Copyright (c) Metric and the Snowflake Authors.
// Licensed under the Apache License, Version 2.0.

using System;
using VCDiff.Includes;
using VCDiff.Shared;

namespace VCDiff.Encoders;

/// <summary>
///     Maps (instruction, size, mode) to the opcodes of the default code table, for single instructions and for the
///     second half of compound opcodes.
/// </summary>
internal sealed class InstructionMap
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
				this.opcodes2[first] = instmode;
			}

			var sizeArray = instmode[inst + mode];
			if (sizeArray == null)
			{
				sizeArray = NewSizeOpcodeArray(this.maxSize + 1);
				instmode[inst + mode] = sizeArray;
			}

			if (sizeArray[size] == CodeTable.KNoOpcode) sizeArray[size] = opcode;
		}

		private static int[] NewSizeOpcodeArray(int size)
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

	public static readonly InstructionMap Instance = new();

	private readonly OpcodeMap firstMap;
	private readonly OpcodeMap2 secondMap;

	private InstructionMap()
	{
		var table = CodeTable.DefaultTable;
		var inst2 = table.Inst2;
		var inst1 = table.Inst1;
		var size2 = table.Size2;
		var size1 = table.Size1;
		var mode1 = table.Mode1;
		var mode2 = table.Mode2;

		this.firstMap = new OpcodeMap((int)VcDiffInstructionType.LAST + AddressCache.DEFAULT_LAST + 1, MaxSize(size1));
		this.secondMap = new OpcodeMap2((int)VcDiffInstructionType.LAST + AddressCache.DEFAULT_LAST + 1, MaxSize(size2));

		for (var opcode = 0; opcode < CodeTable.KCodeTableSize; ++opcode)
		{
			if (inst2[opcode] == CodeTable.N)
				this.firstMap.Add(inst1[opcode], size1[opcode], mode1[opcode], (byte)opcode);
			else if (inst1[opcode] == CodeTable.N) this.firstMap.Add(inst2[opcode], size2[opcode], mode2[opcode], (byte)opcode);
		}

		for (var opcode = 0; opcode < CodeTable.KCodeTableSize; ++opcode)
		{
			if (inst1[opcode] != CodeTable.N && inst2[opcode] != CodeTable.N)
			{
				var found = this.LookFirstOpcode(inst1[opcode], size1[opcode], mode1[opcode]);
				if (found == CodeTable.KNoOpcode) continue;

				this.secondMap.Add((byte)found, inst2[opcode], size2[opcode], mode2[opcode], (byte)opcode);
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

	private static byte MaxSize(ReadOnlySpan<byte> sizes)
	{
		byte maxSize = 0;
		foreach (var size in sizes)
		{
			if (maxSize < size)
				maxSize = size;
		}

		return maxSize;
	}
}
