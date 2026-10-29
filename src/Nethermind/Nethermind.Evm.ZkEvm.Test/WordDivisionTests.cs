// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Numerics;
using Nethermind.Int256;
using NUnit.Framework;

namespace Nethermind.Evm.ZkEvm.Test;

public class WordDivisionTests
{
    /// <remarks>
    /// The operands take every number of significant 32-bit digits, and digits next to their boundaries, where the
    /// quotient estimate needs its corrections and the add-back step runs.
    /// </remarks>
    [Test]
    public void Matches_the_library_division([Range(0, 7)] int seed)
    {
        Random random = new(seed);
        List<string> mismatches = [];
        for (int i = 0; i < 20_000 && mismatches.Count < 5; i++)
        {
            UInt256 dividend = Operand(random);
            UInt256 divisor = Operand(random);
            if (divisor.IsZero) divisor = UInt256.One;

            WordDivision.DivRem(in dividend, in divisor, out UInt256 quotient, out UInt256 remainder);
            UInt256.Divide(in dividend, in divisor, out UInt256 expectedQuotient);
            UInt256.Mod(in dividend, in divisor, out UInt256 expectedRemainder);
            if (quotient != expectedQuotient || remainder != expectedRemainder)
                mismatches.Add($"{dividend:x} / {divisor:x}: {quotient:x} r {remainder:x}, expected {expectedQuotient:x} r {expectedRemainder:x}");
        }

        Assert.That(mismatches, Is.Empty);
    }

    /// <remarks>The dividends are 512-bit products of such operands, as MULMOD forms them.</remarks>
    [Test]
    public unsafe void Remainder_matches_the_library_multiply_mod([Range(0, 7)] int seed)
    {
        Random random = new(seed);
        List<string> mismatches = [];
        uint* product = stackalloc uint[16];
        for (int i = 0; i < 20_000 && mismatches.Count < 5; i++)
        {
            UInt256 a = Operand(random);
            UInt256 b = Operand(random);
            UInt256 modulus = Operand(random);
            if (modulus.IsZero) modulus = UInt256.One;

            byte[] productBytes = ((BigInteger)a * (BigInteger)b).ToByteArray(isUnsigned: true);
            new Span<uint>(product, 16).Clear();
            productBytes.CopyTo(new Span<byte>(product, 64));

            WordDivision.Remainder(product, in modulus, out UInt256 remainder);
            UInt256.MultiplyMod(in a, in b, in modulus, out UInt256 expected);
            if (remainder != expected)
                mismatches.Add($"{a:x} * {b:x} mod {modulus:x}: {remainder:x}, expected {expected:x}");
        }

        Assert.That(mismatches, Is.Empty);
    }

    private static UInt256 Operand(Random random)
    {
        Span<uint> digits = stackalloc uint[8];
        int count = random.Next(0, 9);
        for (int i = 0; i < count; i++)
        {
            digits[i] = random.Next(6) switch
            {
                0 => 0,
                1 => uint.MaxValue,
                2 => 0x8000_0000,
                3 => 0x7FFF_FFFF,
                4 => (uint)random.Next(1, 4),
                _ => (uint)random.NextInt64(0, 1L << 32),
            };
        }

        if (count > 0 && digits[count - 1] == 0) digits[count - 1] = 1;
        return new UInt256(
            digits[0] | (ulong)digits[1] << 32,
            digits[2] | (ulong)digits[3] << 32,
            digits[4] | (ulong)digits[5] << 32,
            digits[6] | (ulong)digits[7] << 32);
    }
}
