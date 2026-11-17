// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nethermind.Int256;

namespace Nethermind.Evm;

/// <summary>MULMOD by a recurring odd modulus as two Montgomery products on Airbender's u256 delegation.</summary>
/// <remarks>
/// A contract that reduces many products, such as a SNARK verifier, takes them modulo one or two primes, and
/// <see cref="WordDivision"/> takes thousands of cycles to divide each 512-bit product. With <c>R = 2^256</c> and an
/// odd <c>m</c>, <c>mont(x, y) = x y / R mod m</c> is six delegations, and <c>a b mod m = mont(mont(a, b), R^2 mod m)</c>.
/// The constants of a modulus, <c>m^-1 mod R</c> and <c>R^2 mod m</c>, cost about two divisions, so a modulus gets them
/// the second time it is seen; until then, and for an even modulus or a factor not below it, the caller divides.
/// </remarks>
internal static unsafe class AirbenderMontgomery
{
    private const int Known = 4;
    private const int Seen = 4;
    private const int WordsPerEntry = 3;

    // Known entries of m, m^-1 mod R and R^2 mod m, then the moduli seen once, in RAM where the delegation reads them.
    private static UInt256* s_table;
    private static int s_nextKnown;
    private static int s_nextSeen;

    /// <summary>Takes <c>(a * b) mod m</c> by Montgomery products, if <paramref name="m"/> is odd and known and both factors are below it.</summary>
    [SkipLocalsInit]
    public static bool TryMultiply(in UInt256 a, in UInt256 b, in UInt256 m, out UInt256 result)
    {
        UInt256* entry;
        if ((m.u0 & 1) == 0 || a >= m || b >= m || (entry = Find(in m)) is null)
        {
            Unsafe.SkipInit(out result);
            return false;
        }

        byte* buffer = stackalloc byte[3 * 32 + 31];
        UInt256* words = (UInt256*)(((nuint)buffer + 31) & ~(nuint)31);
        words[0] = a;
        words[1] = b;
        Multiply(words, words + 1, words + 2, entry);
        Multiply(words, entry + 2, words + 2, entry);
        result = words[0];
        return true;
    }

    /// <summary>Replaces <paramref name="x"/> with <c>x y / R mod m</c>, for <paramref name="x"/> and <paramref name="y"/> below m.</summary>
    /// <remarks>
    /// With <c>q = lo(x y) m^-1 mod R</c>, <c>q m</c> has the low half of <c>x y</c>, so <c>(x y - q m) / R</c> is
    /// <c>hi(x y) - hi(q m)</c>, which needs no carry from the low halves and lies in <c>(-m, m)</c>.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Multiply(UInt256* x, UInt256* y, UInt256* scratch, UInt256* entry)
    {
        AirbenderArithmetic.Copy(scratch, x);
        AirbenderArithmetic.MultiplyLow(scratch, y);
        AirbenderArithmetic.MultiplyLow(scratch, entry + 1);
        AirbenderArithmetic.MultiplyHigh(scratch, entry);
        AirbenderArithmetic.MultiplyHigh(x, y);
        if (AirbenderArithmetic.Subtract(x, scratch))
            AirbenderArithmetic.Add(x, entry);
    }

    /// <summary>The entry of <paramref name="m"/>, made if it was seen before, or null.</summary>
    private static UInt256* Find(in UInt256 m)
    {
        UInt256* table = s_table;
        if (table is null)
        {
            const int Size = (Known * WordsPerEntry + Seen) * 32;
            table = (UInt256*)NativeMemory.AlignedAlloc(Size, 32);
            NativeMemory.Clear(table, Size);
            s_table = table;
        }

        // An unused entry is zero, which no odd modulus matches.
        for (int i = 0; i < Known; i++)
        {
            if (table[i * WordsPerEntry] == m)
                return table + i * WordsPerEntry;
        }

        UInt256* seen = table + Known * WordsPerEntry;
        for (int i = 0; i < Seen; i++)
        {
            if (seen[i] == m)
                return Prepare(in m);
        }

        seen[s_nextSeen] = m;
        s_nextSeen = (s_nextSeen + 1) % Seen;
        return null;
    }

    /// <summary>Fills the next known entry, in turn, with <paramref name="m"/> and its constants.</summary>
    [SkipLocalsInit]
    private static UInt256* Prepare(in UInt256 m)
    {
        UInt256* entry = s_table + s_nextKnown * WordsPerEntry;
        s_nextKnown = (s_nextKnown + 1) % Known;

        byte* buffer = stackalloc byte[3 * 32 + 31];
        UInt256* words = (UInt256*)(((nuint)buffer + 31) & ~(nuint)31);

        // m^-1 mod R by Newton: an odd m is its own inverse mod 8, and each step x (2 - m x) doubles the bits.
        entry[0] = m;
        entry[1] = m;
        for (int step = 0; step < 7; step++)
        {
            words[0] = m;
            AirbenderArithmetic.MultiplyLow(words, entry + 1);
            words[1] = new UInt256(2);
            AirbenderArithmetic.Subtract(words + 1, words);
            AirbenderArithmetic.MultiplyLow(entry + 1, words + 1);
        }

        // R mod m is (R - m) mod m, and R^2 mod m the remainder of its square.
        UInt256.Subtract(default, in m, out UInt256 negated);
        WordDivision.DivRem(in negated, in m, out _, out UInt256 r);
        words[0] = r;
        words[1] = r;
        words[2] = r;
        AirbenderArithmetic.MultiplyLow(words, words + 2);
        AirbenderArithmetic.MultiplyHigh(words + 1, words + 2);
        WordDivision.Remainder((uint*)words, in m, out entry[2]);
        return entry;
    }
}
