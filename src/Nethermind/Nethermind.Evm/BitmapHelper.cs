// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

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

            HandleNumbits(numbits, bitvec, ref pc);
        }
        return bitvec;
    }

    public static void HandleNumbits(int numbits, byte[] bitvec, scoped ref int pc)
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
    public static bool IsCodeSegment(byte[] bitvec, int pos)
    {
        return (bitvec[pos / 8] & (0x80 >> (pos % 8))) == 0;
    }

    private static void Set1(this byte[] bitvec, int pos)
    {
        bitvec[pos / 8] |= _lookup[pos % 8];
    }

    private static void SetN(this byte[] bitvec, int pos, UInt16 flag)
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

    private static void Set8(this byte[] bitvec, int pos)
    {
        byte a = (byte)(0xFF >> (pos % 8));
        bitvec[pos / 8] |= a;
        bitvec[pos / 8 + 1] = (byte)~a;
    }

    private static void Set16(this byte[] bitvec, int pos)
    {
        byte a = (byte)(0xFF >> (pos % 8));
        bitvec[pos / 8] |= a;
        bitvec[pos / 8 + 1] = 0xFF;
        bitvec[pos / 8 + 2] = (byte)~a;
    }

    private const uint Vector128ByteCount = 16;
    private const uint Vector128IntCount = 4;
    private const uint Vector256ByteCount = 32;
    private const uint Vector256IntCount = 8;

    public static bool CheckCollision(byte[] codeSegments, byte[] jumpmask)
    {
        int count = Math.Min(codeSegments.Length, jumpmask.Length);

        uint i = 0;

        ref byte left = ref MemoryMarshal.GetReference<byte>(codeSegments);
        ref byte right = ref MemoryMarshal.GetReference<byte>(jumpmask);

        if (Vector256.IsHardwareAccelerated)
        {
            Vector256<byte> zeros = Vector256.Create<byte>(0);
            for (; i < (uint)count - (Vector256IntCount - 1u); i += Vector256IntCount)
            {
                Vector256<byte> result = Vector256.LoadUnsafe(ref left, i) & Vector256.LoadUnsafe(ref right, i);
                result = Vector256.Min(result, zeros);
                if (Vector256.Sum(result) != 0)
                {
                    return true;
                }
            }
        }
        else if (Vector128.IsHardwareAccelerated)
        {
            Vector128<byte> zeros = Vector128.Create<byte>(0);
            for (; i < (uint)count - (Vector128IntCount - 1u); i += Vector128IntCount)
            {
                Vector128<byte> result = Vector128.LoadUnsafe(ref left, i) & Vector128.LoadUnsafe(ref right, i);
                result = Vector128.Min(result, zeros);
                if (Vector128.Sum(result) != 0)
                {
                    return true;
                }
            }
        }

        for (int j = (int)i; j < (uint)count; j++)
        {
            if ((codeSegments[j] & jumpmask[j]) != 0)
            {
                return true;
            }
        }

        return false;
    }
}
