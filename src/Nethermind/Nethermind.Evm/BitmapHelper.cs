// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Evm;
public static class BitmapHelper
{
    private const ushort Set2BitsMask = 0b1100_0000_0000_0000;
    private const ushort Set3BitsMask = 0b1110_0000_0000_0000;
    private const ushort Set4BitsMask = 0b1111_0000_0000_0000;
    private const ushort Set5BitsMask = 0b1111_1000_0000_0000;
    private const ushort Set6BitsMask = 0b1111_1100_0000_0000;
    private const ushort Set7BitsMask = 0b1111_1110_0000_0000;

    private static readonly byte[] _lookup = { 0x80, 0x40, 0x20, 0x10, 0x8, 0x4, 0x2, 0x1 };

    /// <summary>
    /// Collects data locations in code.
    /// An unset bit means the byte is an opcode, a set bit means it's data.
    /// </summary>
    public static byte[] CreateCodeBitmap(ReadOnlySpan<byte> code, bool isEof = false)
    {
        // The bitmap is 4 bytes longer than necessary, in case the code
        // ends with a PUSH32, the algorithm will push zeroes onto the
        // bitvector outside the bounds of the actual code.
        byte[] bitvec = new byte[(code.Length / 8) + 1 + 4];
        Span<byte> bitvecSpan = bitvec.AsSpan();

        for (int pc = 0; pc < code.Length;)
        {
            Instruction op = (Instruction)code[pc];
            pc++;

            int numbits = op switch
            {
                Instruction.RJUMPV => isEof ? op.GetImmediateCount(isEof, code[pc]) : 0,
                _ => op.GetImmediateCount(isEof),
            };

            if (numbits == 0) continue;

            HandleNumbits(numbits, ref bitvecSpan, ref pc);
        }
        return bitvec;
    }

    public static void HandleNumbits(int numbits, scoped ref Span<byte> bitvec, scoped ref int pc)
    {
        if (numbits >= 8)
        {
            for (; numbits >= 16; numbits -= 16)
            {
                bitvec.Set16(pc);
                pc += 16;
            }

            for (; numbits >= 8; numbits -= 8)
            {
                bitvec.Set8(pc);
                pc += 8;
            }
        }

        switch (numbits)
        {
            case 1:
                bitvec.Set1(pc);
                pc += 1;
                break;
            case 2:
                bitvec.SetN(pc, Set2BitsMask);
                pc += 2;
                break;
            case 3:
                bitvec.SetN(pc, Set3BitsMask);
                pc += 3;
                break;
            case 4:
                bitvec.SetN(pc, Set4BitsMask);
                pc += 4;
                break;
            case 5:
                bitvec.SetN(pc, Set5BitsMask);
                pc += 5;
                break;
            case 6:
                bitvec.SetN(pc, Set6BitsMask);
                pc += 6;
                break;
            case 7:
                bitvec.SetN(pc, Set7BitsMask);
                pc += 7;
                break;
        }
    }
    /// <summary>
    /// Checks if the position is in a code segment.
    /// </summary>
    public static bool IsCodeSegment(ref Span<byte> bitvec, int pos)
    {
        return (bitvec[pos / 8] & (0x80 >> (pos % 8))) == 0;
    }

    public static bool IsCodeSegment(byte[] bitvec, int pos)
    {
        return (bitvec[pos / 8] & (0x80 >> (pos % 8))) == 0;
    }

    private static void Set1(this ref Span<byte> bitvec, int pos)
    {
        bitvec[pos / 8] |= _lookup[pos % 8];
    }

    private static void SetN(this ref Span<byte> bitvec, int pos, UInt16 flag)
    {
        ushort a = (ushort)(flag >> (pos % 8));
        bitvec[pos / 8] |= (byte)(a >> 8);
        byte b = (byte)a;
        if (b != 0)
        {
            //	If the bit-setting affects the neighbouring byte, we can assign - no need to OR it,
            //	since it's the first write to that byte
            bitvec[pos / 8 + 1] = b;
        }
    }

    private static void Set8(this ref Span<byte> bitvec, int pos)
    {
        byte a = (byte)(0xFF >> (pos % 8));
        bitvec[pos / 8] |= a;
        bitvec[pos / 8 + 1] = (byte)~a;
    }

    private static void Set16(this ref Span<byte> bitvec, int pos)
    {
        byte a = (byte)(0xFF >> (pos % 8));
        bitvec[pos / 8] |= a;
        bitvec[pos / 8 + 1] = 0xFF;
        bitvec[pos / 8 + 2] = (byte)~a;
    }
}
