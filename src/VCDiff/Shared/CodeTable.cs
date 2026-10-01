// Copyright (c) Metric and the Snowflake Authors.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Buffers;
using VCDiff.Includes;

namespace VCDiff.Shared;

internal sealed class CodeTable
{
	public const byte A = (byte)VcDiffInstructionType.ADD;
	public const byte C = (byte)VcDiffInstructionType.COPY;

	public const byte N = (byte)VcDiffInstructionType.NOOP;
	private const byte R = (byte)VcDiffInstructionType.RUN;

	public const int KCodeTableSize = 256;
	/// <summary>
	///     Marker for "no opcode" (outside the byte range of real opcodes).
	/// </summary>
	public const int KNoOpcode = 0x100;
	private static readonly byte[] DefaultInst1 = {
		R, // opcode 0
		A, A, A, A, A, A, A, A, A, A, A, A, A, A, A, A, A, A, // opcodes 1-18
		C, C, C, C, C, C, C, C, C, C, C, C, C, C, C, C, // opcodes 19-34
		C, C, C, C, C, C, C, C, C, C, C, C, C, C, C, C, // opcodes 35-50
		C, C, C, C, C, C, C, C, C, C, C, C, C, C, C, C, // opcodes 51-66
		C, C, C, C, C, C, C, C, C, C, C, C, C, C, C, C, // opcodes 67-82
		C, C, C, C, C, C, C, C, C, C, C, C, C, C, C, C, // opcodes 83-98
		C, C, C, C, C, C, C, C, C, C, C, C, C, C, C, C, // opcodes 99-114
		C, C, C, C, C, C, C, C, C, C, C, C, C, C, C, C, // opcodes 115-130
		C, C, C, C, C, C, C, C, C, C, C, C, C, C, C, C, // opcodes 131-146
		C, C, C, C, C, C, C, C, C, C, C, C, C, C, C, C, // opcodes 147-162
		A, A, A, A, A, A, A, A, A, A, A, A, // opcodes 163-174
		A, A, A, A, A, A, A, A, A, A, A, A, // opcodes 175-186
		A, A, A, A, A, A, A, A, A, A, A, A, // opcodes 187-198
		A, A, A, A, A, A, A, A, A, A, A, A, // opcodes 199-210
		A, A, A, A, A, A, A, A, A, A, A, A, // opcodes 211-222
		A, A, A, A, A, A, A, A, A, A, A, A, // opcodes 223-234
		A, A, A, A, // opcodes 235-238
		A, A, A, A, // opcodes 239-242
		A, A, A, A, // opcodes 243-246
		C, C, C, C, C, C, C, C, C // opcodes 247-255
	};

	private static readonly byte[] DefaultInst2 = {
		N, // opcode 0
		N, N, N, N, N, N, N, N, N, N, N, N, N, N, N, N, N, N, // opcodes 1-18
		N, N, N, N, N, N, N, N, N, N, N, N, N, N, N, N, // opcodes 19-34
		N, N, N, N, N, N, N, N, N, N, N, N, N, N, N, N, // opcodes 35-50
		N, N, N, N, N, N, N, N, N, N, N, N, N, N, N, N, // opcodes 51-66
		N, N, N, N, N, N, N, N, N, N, N, N, N, N, N, N, // opcodes 67-82
		N, N, N, N, N, N, N, N, N, N, N, N, N, N, N, N, // opcodes 83-98
		N, N, N, N, N, N, N, N, N, N, N, N, N, N, N, N, // opcodes 99-114
		N, N, N, N, N, N, N, N, N, N, N, N, N, N, N, N, // opcodes 115-130
		N, N, N, N, N, N, N, N, N, N, N, N, N, N, N, N, // opcodes 131-146
		N, N, N, N, N, N, N, N, N, N, N, N, N, N, N, N, // opcodes 147-162
		C, C, C, C, C, C, C, C, C, C, C, C, // opcodes 163-174
		C, C, C, C, C, C, C, C, C, C, C, C, // opcodes 175-186
		C, C, C, C, C, C, C, C, C, C, C, C, // opcodes 187-198
		C, C, C, C, C, C, C, C, C, C, C, C, // opcodes 199-210
		C, C, C, C, C, C, C, C, C, C, C, C, // opcodes 211-222
		C, C, C, C, C, C, C, C, C, C, C, C, // opcodes 223-234
		C, C, C, C, // opcodes 235-238
		C, C, C, C, // opcodes 239-242
		C, C, C, C, // opcodes 243-246
		A, A, A, A, A, A, A, A, A // opcodes 247-255
	};

	private static readonly byte[] DefaultMode1 = {
		0, // opcode 0
		0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, // opcodes 1-18
		0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, // opcodes 19-34
		1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, // opcodes 35-50
		2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, // opcodes 51-66
		3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, // opcodes 67-82
		4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, // opcodes 83-98
		5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, // opcodes 99-114
		6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, // opcodes 115-130
		7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, // opcodes 131-146
		8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, // opcodes 147-162
		0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, // opcodes 163-174
		0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, // opcodes 175-186
		0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, // opcodes 187-198
		0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, // opcodes 199-210
		0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, // opcodes 211-222
		0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, // opcodes 223-234
		0, 0, 0, 0, // opcodes 235-238
		0, 0, 0, 0, // opcodes 239-242
		0, 0, 0, 0, // opcodes 243-246
		0, 1, 2, 3, 4, 5, 6, 7, 8 // opcodes 247-255
	};

	private static readonly byte[] DefaultMode2 = {
		0, // opcode 0
		0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, // opcodes 1-18
		0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, // opcodes 19-34
		0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, // opcodes 35-50
		0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, // opcodes 51-66
		0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, // opcodes 67-82
		0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, // opcodes 83-98
		0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, // opcodes 99-114
		0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, // opcodes 115-130
		0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, // opcodes 131-146
		0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, // opcodes 147-162
		0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, // opcodes 163-174
		1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, // opcodes 175-186
		2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, // opcodes 187-198
		3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, // opcodes 199-210
		4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, // opcodes 211-222
		5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, // opcodes 223-234
		6, 6, 6, 6, // opcodes 235-238
		7, 7, 7, 7, // opcodes 239-242
		8, 8, 8, 8, // opcodes 243-246
		0, 0, 0, 0, 0, 0, 0, 0, 0 // opcodes 247-255
	};

	private static readonly byte[] DefaultSize1 = {
		0, // opcode 0
		0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, // 1-18
		0, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, // 19-34
		0, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, // 35-50
		0, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, // 51-66
		0, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, // 67-82
		0, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, // 83-98
		0, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, // 99-114
		0, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, // 115-130
		0, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, // 131-146
		0, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, // 147-162
		1, 1, 1, 2, 2, 2, 3, 3, 3, 4, 4, 4, // opcodes 163-174
		1, 1, 1, 2, 2, 2, 3, 3, 3, 4, 4, 4, // opcodes 175-186
		1, 1, 1, 2, 2, 2, 3, 3, 3, 4, 4, 4, // opcodes 187-198
		1, 1, 1, 2, 2, 2, 3, 3, 3, 4, 4, 4, // opcodes 199-210
		1, 1, 1, 2, 2, 2, 3, 3, 3, 4, 4, 4, // opcodes 211-222
		1, 1, 1, 2, 2, 2, 3, 3, 3, 4, 4, 4, // opcodes 223-234
		1, 2, 3, 4, // opcodes 235-238
		1, 2, 3, 4, // opcodes 239-242
		1, 2, 3, 4, // opcodes 243-246
		4, 4, 4, 4, 4, 4, 4, 4, 4 // opcodes 247-255
	};

	private static readonly byte[] DefaultSize2 = {
		0, // opcode 0
		0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, // opcodes 1-18
		0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, // opcodes 19-34
		0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, // opcodes 35-50
		0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, // opcodes 51-66
		0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, // opcodes 67-82
		0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, // opcodes 83-98
		0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, // opcodes 99-114
		0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, // opcodes 115-130
		0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, // opcodes 131-146
		0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, // opcodes 147-162
		4, 5, 6, 4, 5, 6, 4, 5, 6, 4, 5, 6, // opcodes 163-174
		4, 5, 6, 4, 5, 6, 4, 5, 6, 4, 5, 6, // opcodes 175-186
		4, 5, 6, 4, 5, 6, 4, 5, 6, 4, 5, 6, // opcodes 187-198
		4, 5, 6, 4, 5, 6, 4, 5, 6, 4, 5, 6, // opcodes 199-210
		4, 5, 6, 4, 5, 6, 4, 5, 6, 4, 5, 6, // opcodes 211-222
		4, 5, 6, 4, 5, 6, 4, 5, 6, 4, 5, 6, // opcodes 223-234
		4, 4, 4, 4, // opcodes 235-238
		4, 4, 4, 4, // opcodes 239-242
		4, 4, 4, 4, // opcodes 243-246
		1, 1, 1, 1, 1, 1, 1, 1, 1 // opcodes 247-255
	};

	// Serialized layout (RFC 3284 section 7 / open-vcdiff VCDiffCodeTableData):
	// inst1[256], inst2[256], size1[256], size2[256], mode1[256], mode2[256].
	private const int Inst1Offset = 0;
	private const int Inst2Offset = KCodeTableSize;
	private const int Size1Offset = KCodeTableSize * 2;
	private const int Size2Offset = KCodeTableSize * 3;
	private const int Mode1Offset = KCodeTableSize * 4;
	private const int Mode2Offset = KCodeTableSize * 5;
	public const int SerializedSize = KCodeTableSize * 6;

	/// <summary>
	///     The RFC 3284 default code table serialized as 1536 bytes. Also the dictionary a custom code table is
	///     delta-encoded against.
	/// </summary>
	private static readonly byte[] DefaultSerialized = BuildDefault();

	/// <summary>
	///     The shared, immutable default code table.
	/// </summary>
	public static readonly CodeTable DefaultTable = new(DefaultSerialized);

	// single storage for all six rows; never mutated after construction
	private readonly byte[] table;

	private CodeTable(byte[] table)
	{
		this.table = table;
	}

	public ReadOnlySpan<byte> Inst1 => new(this.table, Inst1Offset, KCodeTableSize);
	public ReadOnlySpan<byte> Inst2 => new(this.table, Inst2Offset, KCodeTableSize);
	public ReadOnlySpan<byte> Size1 => new(this.table, Size1Offset, KCodeTableSize);
	public ReadOnlySpan<byte> Size2 => new(this.table, Size2Offset, KCodeTableSize);
	public ReadOnlySpan<byte> Mode1 => new(this.table, Mode1Offset, KCodeTableSize);
	public ReadOnlySpan<byte> Mode2 => new(this.table, Mode2Offset, KCodeTableSize);

	/// <summary>
	///     The serialized default code table (read-only view), used as the dictionary when decoding a custom code table.
	/// </summary>
	public static ReadOnlyMemory<byte> DefaultBytes => DefaultSerialized;

	private static byte[] BuildDefault()
	{
		var bytes = new byte[SerializedSize];
		DefaultInst1.CopyTo(bytes, Inst1Offset);
		DefaultInst2.CopyTo(bytes, Inst2Offset);
		DefaultSize1.CopyTo(bytes, Size1Offset);
		DefaultSize2.CopyTo(bytes, Size2Offset);
		DefaultMode1.CopyTo(bytes, Mode1Offset);
		DefaultMode2.CopyTo(bytes, Mode2Offset);
		return bytes;
	}

	/// <summary>
	///     Creates a code table from its 1536-byte serialization. Fails when the length is wrong or an entry holds an
	///     instruction type outside NOOP/ADD/RUN/COPY.
	/// </summary>
	public static bool TryCreate(ReadOnlySpan<byte> serialized, out CodeTable? codeTable)
	{
		codeTable = null;
		if (serialized.Length != SerializedSize) return false;

		var bytes = serialized.ToArray();
		if (!AreInstructionTypesValid(bytes)) return false;

		codeTable = new CodeTable(bytes);
		return true;
	}

	private static bool AreInstructionTypesValid(byte[] bytes)
	{
		for (var i = Inst1Offset; i < Inst2Offset + KCodeTableSize; i++)
		{
			if (bytes[i] > C) return false;
		}

		return true;
	}

	/// <summary>
	///     Checks every COPY entry uses an address mode that exists for the given address cache sizes.
	/// </summary>
	public bool AreModesValid(byte nearSize, byte sameSize)
	{
		var lastMode = (int)VcDiffModes.FIRST + nearSize + sameSize - 1;
		for (var opcode = 0; opcode < KCodeTableSize; opcode++)
		{
			if (this.table[Inst1Offset + opcode] == C && this.table[Mode1Offset + opcode] > lastMode) return false;
			if (this.table[Inst2Offset + opcode] == C && this.table[Mode2Offset + opcode] > lastMode) return false;
		}

		return true;
	}
}
