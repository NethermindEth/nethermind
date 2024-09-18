// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace Nethermind.Evm;
public static class BitmapHelper
{
    private static readonly byte[] _lookup =
    {
        0b0000_0000,
        0b0000_0001,
        0b0000_0011,
        0b0000_0111,
        0b0000_1111,
        0b0001_1111,
        0b0011_1111,
        0b0111_1111
    };

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
            var opMetadaata = ((Instruction)code[pc]).StackRequirements();

            pc++;

            int numbits =
                code[pc] == (byte)Instruction.RJUMPV
                    ? Instruction.RJUMPV.GetImmediateCount(isEof, code[pc])
                    : opMetadaata.immediates.Value;

            if (numbits == 0) continue;

            HandleNumbits(numbits, bitvec, ref pc);
        }
        return bitvec;
    }

    public static void HandleNumbits(int numbits, Span<byte> bitvec, scoped ref int pc)
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

        if (numbits > 1)
        {
            bitvec.SetN(pc, _lookup[numbits]);
            pc += numbits;
        }
        else
        {
            bitvec.Set1(pc);
            pc += numbits;
        }
    }
    /// <summary>
    /// Checks if the position is in a code segment.
    /// </summary>
    public static bool IsCodeSegment(Span<byte> bitvec, int pos)
    {
        return (bitvec[pos / 8] & (0x80 >> (pos % 8))) == 0;
    }

    private static void Set1(this Span<byte> bitvec, int pos)
    {
        bitvec[pos / 8] |= (byte)(1 << (pos % 8));
    }

    private static void SetN(this Span<byte> bitvec, int pos, ushort flag)
    {
        ushort a = (ushort)(flag << (pos % 8));
        bitvec[pos / 8] |= (byte)a;
        byte b = (byte)(a >> 8);
        if (b != 0)
        {
            //	If the bit-setting affects the neighbouring byte, we can assign - no need to OR it,
            //	since it's the first write to that byte
            bitvec[pos / 8 + 1] = b;
        }
    }

    private static void Set8(this Span<byte> bitvec, int pos)
    {
        byte a = (byte)(0xFF << (pos % 8));
        bitvec[pos / 8] |= a;
        bitvec[pos / 8 + 1] = (byte)~a;
    }

    private static void Set16(this Span<byte> bitvec, int pos)
    {
        byte a = (byte)(0xFF << (pos % 8));
        bitvec[pos / 8] |= a;
        bitvec[pos / 8 + 1] = 0xFF;
        bitvec[pos / 8 + 2] = (byte)~a;
    }

    public static bool CheckCollision(ReadOnlySpan<byte> codeSegments, ReadOnlySpan<byte> jumpmask)
    {
        int count = Math.Min(codeSegments.Length, jumpmask.Length);

        int i = 0;

        ref byte left = ref MemoryMarshal.GetReference<byte>(codeSegments);
        ref byte right = ref MemoryMarshal.GetReference<byte>(jumpmask);

        if (Vector256.IsHardwareAccelerated && count >= Vector256<byte>.Count)
        {
            for (; (uint)(i + Vector256<byte>.Count) <= (uint)count; i += Vector256<byte>.Count)
            {
                Vector256<byte> result = Vector256.LoadUnsafe(ref left, (uint)i) & Vector256.LoadUnsafe(ref right, (uint)i);
                if (result != default)
                {
                    return true;
                }
            }
        }
        else if (Vector128.IsHardwareAccelerated && count >= Vector128<byte>.Count)
        {
            for (; (i + Vector128<byte>.Count) <= (uint)count; i += Vector128<byte>.Count)
            {
                Vector128<byte> result = Vector128.LoadUnsafe(ref left, (uint)i) & Vector128.LoadUnsafe(ref right, (uint)i);
                if (result != default)
                {
                    return true;
                }
            }
        }

        for (; i < count; i++)
        {
            if ((codeSegments[i] & jumpmask[i]) != 0)
            {
                return true;
            }
        }

        return false;
    }
}
