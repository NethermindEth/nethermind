// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
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

            if (bitCount >= 8)
            {
                bitCount -= 8;
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

    public static bool CheckCollision(ReadOnlySpan<byte> codeSegments, ReadOnlySpan<byte> jumpMask)
    {
        nuint count = (nuint)Math.Min(codeSegments.Length, jumpMask.Length);

        ref byte left = ref MemoryMarshal.GetReference<byte>(codeSegments);
        ref byte right = ref MemoryMarshal.GetReference<byte>(jumpMask);

        if (Vector512.IsHardwareAccelerated && count >= (nuint)Vector512<byte>.Count)
        {
            nuint offset = 0;
            nuint lengthToExamine = count - (nuint)Vector512<byte>.Count;
            // Unsigned, so it shouldn't have overflowed larger than length (rather than negative)
            Debug.Assert(lengthToExamine < count);
            if (lengthToExamine != 0)
            {
                do
                {
                    if ((Vector512.LoadUnsafe(ref left, offset) &
                        Vector512.LoadUnsafe(ref right, offset)) != default)
                    {
                        goto Collision;
                    }
                    offset += (nuint)Vector512<byte>.Count;
                } while (lengthToExamine > offset);
            }

            // Do final compare as Vector512<byte>.Count from end rather than start
            if ((Vector512.LoadUnsafe(ref left, lengthToExamine) &
                Vector512.LoadUnsafe(ref right, lengthToExamine)) == default)
            {
                // C# compiler inverts this test, making the outer goto the conditional jmp.
                goto NoCollision;
            }

            // This becomes a conditional jmp forward to not favor it.
            goto Collision;
        }
        else if (Vector256.IsHardwareAccelerated && count >= (nuint)Vector256<byte>.Count)
        {
            nuint offset = 0;
            nuint lengthToExamine = count - (nuint)Vector256<byte>.Count;
            // Unsigned, so it shouldn't have overflowed larger than length (rather than negative)
            Debug.Assert(lengthToExamine < count);
            if (lengthToExamine != 0)
            {
                do
                {
                    if ((Vector256.LoadUnsafe(ref left, offset) &
                        Vector256.LoadUnsafe(ref right, offset)) != default)
                    {
                        goto Collision;
                    }
                    offset += (nuint)Vector256<byte>.Count;
                } while (lengthToExamine > offset);
            }

            // Do final compare as Vector512<byte>.Count from end rather than start
            if ((Vector256.LoadUnsafe(ref left, lengthToExamine) &
                Vector256.LoadUnsafe(ref right, lengthToExamine)) == default)
            {
                // C# compiler inverts this test, making the outer goto the conditional jmp.
                goto NoCollision;
            }

            // This becomes a conditional jmp forward to not favor it.
            goto Collision;
        }
        else if (Vector128.IsHardwareAccelerated && count >= (nuint)Vector128<byte>.Count)
        {
            nuint offset = 0;
            nuint lengthToExamine = count - (nuint)Vector128<byte>.Count;
            // Unsigned, so it shouldn't have overflowed larger than length (rather than negative)
            Debug.Assert(lengthToExamine < count);
            if (lengthToExamine != 0)
            {
                do
                {
                    if ((Vector128.LoadUnsafe(ref left, offset) &
                        Vector128.LoadUnsafe(ref right, offset)) != default)
                    {
                        goto Collision;
                    }
                    offset += (nuint)Vector128<byte>.Count;
                } while (lengthToExamine > offset);
            }

            // Do final compare as Vector512<byte>.Count from end rather than start
            if ((Vector128.LoadUnsafe(ref left, lengthToExamine) &
                Vector128.LoadUnsafe(ref right, lengthToExamine)) == default)
            {
                // C# compiler inverts this test, making the outer goto the conditional jmp.
                goto NoCollision;
            }

            // This becomes a conditional jmp forward to not favor it.
            goto Collision;
        }
        else
        {
            for (nuint i = 0; i < count; i++)
            {
                if ((Unsafe.Add(ref left, i) & Unsafe.Add(ref right, i)) != 0)
                {
                    goto Collision;
                }
            }
        }

    // As there are so many true/false exit points the Jit will coalesce them to one location.
    // We want them at the end so the conditional early exit jmps are all jmp forwards so the
    // branch predictor in a uninitialized state will not take them e.g.
    // - loops are conditional jmps backwards and predicted
    // - exceptions are conditional forwards jmps and not predicted

    // When no collision happens; which is the longest execution, we want it to determine that
    // as fast as possible so we do not want the early outs to be "predicted not taken" branches.
    NoCollision:
        return false;
    Collision:
        return true;
    }
}
