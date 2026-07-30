// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Int256;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

/// <summary>
/// The MULMOD path that folds instead of dividing when the modulus is 2^256-1 must agree with the
/// general implementation everywhere, since a fixed-point helper's 512-bit product - and therefore
/// every amount a pool quote returns - is derived from its result.
/// </summary>
[TestFixture]
public class MulModMaxValueTests
{
    [Test]
    public void Folded_MatchesGeneralImplementation_OnBoundaryOperands()
    {
        UInt256 max = UInt256.MaxValue;
        UInt256[] interesting =
        [
            UInt256.Zero, UInt256.One, 2, max, max - 1,
            new UInt256(0, 0, 0, 1), new UInt256(ulong.MaxValue, 0, 0, 0),
            new UInt256(ulong.MaxValue, ulong.MaxValue, ulong.MaxValue, 0),
        ];

        foreach (UInt256 a in interesting)
        {
            foreach (UInt256 b in interesting)
            {
                UInt256.MultiplyMod(in a, in b, in max, out UInt256 expected);
                EvmInstructions.OpMulMod.Operation(in a, in b, in max, out UInt256 actual);
                Assert.That(actual, Is.EqualTo(expected), $"a={a} b={b}");
            }
        }
    }

    [Test]
    public void Folded_MatchesGeneralImplementation_OnRandomOperands()
    {
        UInt256 max = UInt256.MaxValue;
        Random random = new(20260730);
        byte[] buffer = new byte[32];

        for (int i = 0; i < 20_000; i++)
        {
            random.NextBytes(buffer);
            UInt256 a = new(buffer, isBigEndian: true);
            random.NextBytes(buffer);
            UInt256 b = new(buffer, isBigEndian: true);

            UInt256.MultiplyMod(in a, in b, in max, out UInt256 expected);
            EvmInstructions.OpMulMod.Operation(in a, in b, in max, out UInt256 actual);
            if (actual != expected)
            {
                Assert.Fail($"a={a} b={b}: expected {expected}, got {actual}");
            }
        }
    }

    [Test]
    public void OtherModuli_StillGoThroughTheGeneralImplementation()
    {
        UInt256 a = new(0x2545f4914f6cdd1dUL, 0x8d9e0f1a2b3c4d5fUL, 0x1f123bb5e3b4a6c7UL, 0x45a308f2cdf6f5a2UL);
        UInt256 b = new(0xbfd25e8cd0364141UL, 0xbaaedce6af48a03bUL, 0xfffffffffffffffeUL, 0x7fffffffffffffffUL);

        foreach (UInt256 modulus in new UInt256[] { 1, 2, 1_000_003, UInt256.MaxValue - 1, new(0, 1, 0, 0) })
        {
            UInt256.MultiplyMod(in a, in b, in modulus, out UInt256 expected);
            EvmInstructions.OpMulMod.Operation(in a, in b, in modulus, out UInt256 actual);
            Assert.That(actual, Is.EqualTo(expected), $"modulus {modulus}");
        }
    }
}
