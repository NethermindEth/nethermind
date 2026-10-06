// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Numerics;
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

    /// <summary>
    /// Whether the word <paramref name="left"/> limbs from <paramref name="words"/> is below the word <paramref name="right"/>
    /// limbs from it, both in limb layout.
    /// </summary>
    /// <remarks>Both are addressed off <paramref name="words"/> itself, so each access folds its constant into its own offset.</remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool IsBelow(ref ulong words, nint left, nint right)
    {
        ulong leftLimb = Unsafe.Add(ref words, left + 3);
        ulong rightLimb = Unsafe.Add(ref words, right + 3);
        if (leftLimb != rightLimb) return leftLimb < rightLimb;
        leftLimb = Unsafe.Add(ref words, left + 2);
        rightLimb = Unsafe.Add(ref words, right + 2);
        if (leftLimb != rightLimb) return leftLimb < rightLimb;
        leftLimb = Unsafe.Add(ref words, left + 1);
        rightLimb = Unsafe.Add(ref words, right + 1);
        if (leftLimb != rightLimb) return leftLimb < rightLimb;
        return Unsafe.Add(ref words, left) < Unsafe.Add(ref words, right);
    }

    /// <summary>As <see cref="IsBelow"/>, with both words read as two's complement.</summary>
    /// <remarks>Only the top limbs carry the sign, so they compare signed and the rest as <see cref="IsBelow"/> does.</remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool IsSignedBelow(ref ulong words, nint left, nint right)
    {
        long leftTop = (long)Unsafe.Add(ref words, left + 3);
        long rightTop = (long)Unsafe.Add(ref words, right + 3);
        if (leftTop != rightTop) return leftTop < rightTop;
        ulong leftLimb = Unsafe.Add(ref words, left + 2);
        ulong rightLimb = Unsafe.Add(ref words, right + 2);
        if (leftLimb != rightLimb) return leftLimb < rightLimb;
        leftLimb = Unsafe.Add(ref words, left + 1);
        rightLimb = Unsafe.Add(ref words, right + 1);
        if (leftLimb != rightLimb) return leftLimb < rightLimb;
        return Unsafe.Add(ref words, left) < Unsafe.Add(ref words, right);
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
    /// <param name="value">The low limb of the word.</param>
    /// <param name="shift">The shift, below 256.</param>
    /// <param name="arithmetic">Whether the bits shifted in copy the sign, rather than being zero.</param>
    /// <remarks>
    /// The mirror of <see cref="ShiftLeft"/>, working from the bottom limb up. The top limb takes its own shift, which
    /// brings in the sign of an arithmetic one, so only the limbs above it need the sign spread out.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void ShiftRight(ref ulong value, int shift, bool arithmetic = false)
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
            Unsafe.Add(ref value, 3) = ShiftTopLimb(limb3, bits, arithmetic);
        }
        else if (limbs == 1)
        {
            ulong limb1 = Unsafe.Add(ref value, 1);
            ulong limb2 = Unsafe.Add(ref value, 2);
            value = (limb1 >> bits) | ((limb2 << 1) << carry);
            ulong limb3 = Unsafe.Add(ref value, 3);
            Unsafe.Add(ref value, 1) = (limb2 >> bits) | ((limb3 << 1) << carry);
            Unsafe.Add(ref value, 2) = ShiftTopLimb(limb3, bits, arithmetic);
            Unsafe.Add(ref value, 3) = FillLimb(limb3, arithmetic);
        }
        else if (limbs == 2)
        {
            ulong limb2 = Unsafe.Add(ref value, 2);
            ulong limb3 = Unsafe.Add(ref value, 3);
            value = (limb2 >> bits) | ((limb3 << 1) << carry);
            Unsafe.Add(ref value, 1) = ShiftTopLimb(limb3, bits, arithmetic);
            ulong fill = FillLimb(limb3, arithmetic);
            Unsafe.Add(ref value, 2) = fill;
            Unsafe.Add(ref value, 3) = fill;
        }
        else
        {
            ulong limb3 = Unsafe.Add(ref value, 3);
            value = ShiftTopLimb(limb3, bits, arithmetic);
            ulong fill = FillLimb(limb3, arithmetic);
            Unsafe.Add(ref value, 1) = fill;
            Unsafe.Add(ref value, 2) = fill;
            Unsafe.Add(ref value, 3) = fill;
        }
    }

    /// <summary>The top limb shifted right by <paramref name="bits"/>, modulo 64, copying its sign when <paramref name="arithmetic"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong ShiftTopLimb(ulong limb, int bits, bool arithmetic) =>
        arithmetic ? (ulong)((long)limb >> bits) : limb >> bits;

    /// <summary>The limb a right shift fills in above the top one: its sign spread out when <paramref name="arithmetic"/>, or zero.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong FillLimb(ulong top, bool arithmetic) => arithmetic ? (ulong)((long)top >> 63) : 0;

    /// <summary>Reports whether the word at <paramref name="word"/>, in limb layout, is a power of two, and which.</summary>
    /// <param name="word">The low limb of the word.</param>
    /// <param name="log2">The exponent, below 256, when the word is a power of two.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool TryGetLog2(ref ulong word, out int log2)
    {
        ulong limb0 = word;
        ulong limb1 = Unsafe.Add(ref word, 1);
        ulong limb2 = Unsafe.Add(ref word, 2);
        ulong limb3 = Unsafe.Add(ref word, 3);
        // The highest nonzero limb, its offset in bits, and the limbs below it, which a power of two leaves clear.
        ulong bit;
        int limbBits;
        ulong below;
        if (limb3 != 0)
        {
            bit = limb3;
            limbBits = 192;
            below = limb0 | limb1 | limb2;
        }
        else if (limb2 != 0)
        {
            bit = limb2;
            limbBits = 128;
            below = limb0 | limb1;
        }
        else if (limb1 != 0)
        {
            bit = limb1;
            limbBits = 64;
            below = limb0;
        }
        else
        {
            bit = limb0;
            limbBits = 0;
            below = 0;
        }

        log2 = limbBits + BitOperations.TrailingZeroCount(bit);
        return (below | (bit & (bit - 1))) == 0 && bit != 0;
    }

    /// <summary>Copies the word at <paramref name="source"/> over the one at <paramref name="destination"/>, both in limb layout.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void CopyWord(ref ulong source, ref ulong destination)
    {
        destination = source;
        Unsafe.Add(ref destination, 1) = Unsafe.Add(ref source, 1);
        Unsafe.Add(ref destination, 2) = Unsafe.Add(ref source, 2);
        Unsafe.Add(ref destination, 3) = Unsafe.Add(ref source, 3);
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
