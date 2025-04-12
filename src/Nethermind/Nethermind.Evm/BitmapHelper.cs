// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
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

    public static void FlagMultipleBits(int bitCount, Span<byte> bitVector, scoped ref int pc)
    {
        if (bitCount == 0) return;

        if (bitCount >= 8)
        {
            for (; bitCount >= 16; bitCount -= 16)
            {
                bitVector.Set16(pc);
                pc += 16;
            }

            for (; bitCount >= 8; bitCount -= 8)
            {
                bitVector.Set8(pc);
                pc += 8;
            }
        }

        if (bitCount > 1)
        {
            bitVector.SetN(pc, _lookup[bitCount]);
            pc += bitCount;
        }
        else
        {
            bitVector.Set1(pc);
            pc += bitCount;
        }
    }

    private static void Set1(this Span<byte> bitVector, int pos)
    {
        bitVector[pos / 8] |= (byte)(1 << (pos % 8));
    }

    private static void SetN(this Span<byte> bitVector, int pos, ushort flag)
    {
        ushort a = (ushort)(flag << (pos % 8));
        bitVector[pos / 8] |= (byte)a;
        byte b = (byte)(a >> 8);
        if (b != 0)
        {
            //	If the bit-setting affects the neighboring byte, we can assign - no need to OR it,
            //	since it's the first write to that byte
            bitVector[pos / 8 + 1] = b;
        }
    }

    private static void Set8(this Span<byte> bitVector, int pos)
    {
        byte a = (byte)(0xFF << (pos % 8));
        bitVector[pos / 8] |= a;
        bitVector[pos / 8 + 1] = (byte)~a;
    }

    private static void Set16(this Span<byte> bitVector, int pos)
    {
        byte a = (byte)(0xFF << (pos % 8));
        bitVector[pos / 8] |= a;
        bitVector[pos / 8 + 1] = 0xFF;
        bitVector[pos / 8 + 2] = (byte)~a;
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
