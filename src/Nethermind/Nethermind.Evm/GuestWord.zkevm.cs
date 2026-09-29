// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace Nethermind.Evm;

internal static class GuestWord
{
    /// <summary>Writes <paramref name="value"/>, zero-extended, into the slot whose low limb is <paramref name="word"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void SetWord(ref ulong word, ulong value)
    {
        word = value;
        Unsafe.Add(ref word, 1) = 0;
        Unsafe.Add(ref word, 2) = 0;
        Unsafe.Add(ref word, 3) = 0;
    }

    /// <summary>Whether the word at <paramref name="left"/> is below the word at <paramref name="right"/>, both in limb layout.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool IsBelow(ref ulong left, ref ulong right)
    {
        ulong leftLimb = Unsafe.Add(ref left, 3);
        ulong rightLimb = Unsafe.Add(ref right, 3);
        if (leftLimb != rightLimb) return leftLimb < rightLimb;
        leftLimb = Unsafe.Add(ref left, 2);
        rightLimb = Unsafe.Add(ref right, 2);
        if (leftLimb != rightLimb) return leftLimb < rightLimb;
        leftLimb = Unsafe.Add(ref left, 1);
        rightLimb = Unsafe.Add(ref right, 1);
        if (leftLimb != rightLimb) return leftLimb < rightLimb;
        return left < right;
    }

    /// <summary>Shifts the word at <paramref name="value"/>, in limb layout, left by <paramref name="shift"/> bits, below 256.</summary>
    /// <remarks>
    /// Works from the top limb down, so every limb it reads is still an input limb. <c>(low &gt;&gt; 1) &gt;&gt; (63 - bits)</c>
    /// is <c>low &gt;&gt; (64 - bits)</c> without the count of 64, which would keep <c>low</c> whole when <c>bits</c> is 0.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void ShiftLeft(ref ulong value, int shift)
    {
        // The counts are used unmasked: a 64-bit shift takes its count modulo 64, and 63 ^ shift is 63 - bits.
        int bits = shift;
        int carry = 63 ^ shift;
        int limbs = shift >> 6;
        if (limbs == 0)
        {
            ulong limb3 = Unsafe.Add(ref value, 3);
            ulong limb2 = Unsafe.Add(ref value, 2);
            Unsafe.Add(ref value, 3) = (limb3 << bits) | ((limb2 >> 1) >> carry);
            ulong limb1 = Unsafe.Add(ref value, 1);
            Unsafe.Add(ref value, 2) = (limb2 << bits) | ((limb1 >> 1) >> carry);
            ulong limb0 = value;
            Unsafe.Add(ref value, 1) = (limb1 << bits) | ((limb0 >> 1) >> carry);
            value = limb0 << bits;
        }
        else if (limbs == 1)
        {
            ulong limb2 = Unsafe.Add(ref value, 2);
            ulong limb1 = Unsafe.Add(ref value, 1);
            Unsafe.Add(ref value, 3) = (limb2 << bits) | ((limb1 >> 1) >> carry);
            ulong limb0 = value;
            Unsafe.Add(ref value, 2) = (limb1 << bits) | ((limb0 >> 1) >> carry);
            Unsafe.Add(ref value, 1) = limb0 << bits;
            value = 0;
        }
        else if (limbs == 2)
        {
            ulong limb1 = Unsafe.Add(ref value, 1);
            ulong limb0 = value;
            Unsafe.Add(ref value, 3) = (limb1 << bits) | ((limb0 >> 1) >> carry);
            Unsafe.Add(ref value, 2) = limb0 << bits;
            Unsafe.Add(ref value, 1) = 0;
            value = 0;
        }
        else
        {
            Unsafe.Add(ref value, 3) = value << bits;
            Unsafe.Add(ref value, 2) = 0;
            Unsafe.Add(ref value, 1) = 0;
            value = 0;
        }
    }

    /// <summary>Shifts the word at <paramref name="value"/>, in limb layout, right by <paramref name="shift"/> bits, below 256.</summary>
    /// <remarks>The mirror of <see cref="ShiftLeft"/>, working from the bottom limb up.</remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void ShiftRight(ref ulong value, int shift)
    {
        int bits = shift;
        int carry = 63 ^ shift;
        int limbs = shift >> 6;
        if (limbs == 0)
        {
            ulong limb0 = value;
            ulong limb1 = Unsafe.Add(ref value, 1);
            value = (limb0 >> bits) | ((limb1 << 1) << carry);
            ulong limb2 = Unsafe.Add(ref value, 2);
            Unsafe.Add(ref value, 1) = (limb1 >> bits) | ((limb2 << 1) << carry);
            ulong limb3 = Unsafe.Add(ref value, 3);
            Unsafe.Add(ref value, 2) = (limb2 >> bits) | ((limb3 << 1) << carry);
            Unsafe.Add(ref value, 3) = limb3 >> bits;
        }
        else if (limbs == 1)
        {
            ulong limb1 = Unsafe.Add(ref value, 1);
            ulong limb2 = Unsafe.Add(ref value, 2);
            value = (limb1 >> bits) | ((limb2 << 1) << carry);
            ulong limb3 = Unsafe.Add(ref value, 3);
            Unsafe.Add(ref value, 1) = (limb2 >> bits) | ((limb3 << 1) << carry);
            Unsafe.Add(ref value, 2) = limb3 >> bits;
            Unsafe.Add(ref value, 3) = 0;
        }
        else if (limbs == 2)
        {
            ulong limb2 = Unsafe.Add(ref value, 2);
            ulong limb3 = Unsafe.Add(ref value, 3);
            value = (limb2 >> bits) | ((limb3 << 1) << carry);
            Unsafe.Add(ref value, 1) = limb3 >> bits;
            Unsafe.Add(ref value, 2) = 0;
            Unsafe.Add(ref value, 3) = 0;
        }
        else
        {
            value = Unsafe.Add(ref value, 3) >> bits;
            Unsafe.Add(ref value, 1) = 0;
            Unsafe.Add(ref value, 2) = 0;
            Unsafe.Add(ref value, 3) = 0;
        }
    }

    /// <summary>Loads a big-endian word into a stack slot in limb layout.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void LoadBigEndian(ref ulong slot, ref byte source)
    {
        ulong limb3 = BinaryPrimitives.ReverseEndianness(Unsafe.ReadUnaligned<ulong>(ref source));
        ulong limb2 = BinaryPrimitives.ReverseEndianness(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, 8)));
        ulong limb1 = BinaryPrimitives.ReverseEndianness(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, 16)));
        ulong limb0 = BinaryPrimitives.ReverseEndianness(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, 24)));
        slot = limb0;
        Unsafe.Add(ref slot, 1) = limb1;
        Unsafe.Add(ref slot, 2) = limb2;
        Unsafe.Add(ref slot, 3) = limb3;
    }
}
