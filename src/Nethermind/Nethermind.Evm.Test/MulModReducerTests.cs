// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Int256;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

[Parallelizable(ParallelScope.All)]
public class MulModReducerTests
{
    private static readonly UInt256 Bn254Prime = UInt256.Parse("21888242871839275222246405745257275088548364400416034343698204186575808495617");
    private static readonly UInt256 Secp256k1Prime = UInt256.Parse("115792089237316195423570985008687907853269984665640564039457584007908834671663");

    private static IEnumerable<UInt256> Moduli()
    {
        yield return Bn254Prime;
        yield return Secp256k1Prime;
        yield return UInt256.MaxValue;
        yield return UInt256.MaxValue - 1;
        yield return UInt256.One << 192;
        yield return (UInt256.One << 192) + 1;
        yield return UInt256.One << 255;
        yield return (UInt256.One << 255) - 19;
    }

    [TestCaseSource(nameof(Moduli))]
    public void Matches_the_library_for_random_and_edge_operands(UInt256 modulus)
    {
        MulModReducer reducer = new(in modulus);
        Random random = new(12345);
        UInt256[] edges = [UInt256.Zero, UInt256.One, modulus - 1, modulus, modulus + 1 > modulus ? modulus + 1 : modulus, UInt256.MaxValue, UInt256.MaxValue - 1, UInt256.One << 128];
        foreach (UInt256 x in edges)
        {
            foreach (UInt256 y in edges) AssertMatches(reducer, in x, in y, in modulus);
        }

        Span<byte> bytes = stackalloc byte[32];
        for (int i = 0; i < 50_000; i++)
        {
            random.NextBytes(bytes);
            UInt256 x = new(bytes, isBigEndian: true);
            random.NextBytes(bytes);
            UInt256 y = new(bytes, isBigEndian: true);
            if ((i & 1) == 0)
            {
                // Reduced operands, as field arithmetic passes them.
                UInt256.Mod(in x, in modulus, out x);
                UInt256.Mod(in y, in modulus, out y);
            }

            AssertMatches(reducer, in x, in y, in modulus);
        }
    }

    [Test]
    public void Rejects_a_modulus_below_four_limbs() =>
        Assert.That(() => new MulModReducer(UInt256.MaxValue >> 64), Throws.ArgumentException);

    private static void AssertMatches(MulModReducer reducer, in UInt256 x, in UInt256 y, in UInt256 modulus)
    {
        UInt256.MultiplyMod(in x, in y, in modulus, out UInt256 expected);
        reducer.MultiplyMod(in x, in y, out UInt256 actual);
        Assert.That(actual, Is.EqualTo(expected), $"{x} * {y} mod {modulus}");
    }
}
