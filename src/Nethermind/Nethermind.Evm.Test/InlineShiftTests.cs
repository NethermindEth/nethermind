// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Int256;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

/// <summary>SHL and SHR shift the limbs inline; these pin them to UInt256's own shifts.</summary>
[TestFixture]
public class InlineShiftTests
{
    private static IEnumerable<UInt256> Values()
    {
        yield return UInt256.Zero;
        yield return UInt256.One;
        yield return UInt256.MaxValue;
        yield return new UInt256(0, 0, 0, 1UL << 63);
        yield return new UInt256(ulong.MaxValue, 0, 0, 0);
        yield return new UInt256(0, ulong.MaxValue, 0, 0);
        yield return new UInt256(0, 0, ulong.MaxValue, 0);
        yield return new UInt256(0, 0, 0, ulong.MaxValue);
        yield return new UInt256(0x0123456789abcdefUL, 0xfedcba9876543210UL, 0x0f1e2d3c4b5a6978UL, 0x8796a5b4c3d2e1f0UL);
        Random random = new(20260925);
        byte[] buffer = new byte[32];
        for (int i = 0; i < 200; i++)
        {
            random.NextBytes(buffer);
            yield return new UInt256(buffer, isBigEndian: true);
        }
    }

    [Test]
    public void Shl_matches_UInt256_LeftShift()
    {
        foreach (UInt256 value in Values())
        {
            for (int shift = 0; shift <= 300; shift++)
            {
                UInt256 amount = (ulong)shift;
                EvmInstructions.OpShl.Operation(in amount, in value, out UInt256 actual);
                UInt256 expected = UInt256.Zero;
                if (shift < 256) value.LeftShift(shift, out expected);
                Assert.That(actual, Is.EqualTo(expected), $"{value} << {shift}");
            }
        }
    }

    [Test]
    public void Shr_matches_UInt256_RightShift()
    {
        foreach (UInt256 value in Values())
        {
            for (int shift = 0; shift <= 300; shift++)
            {
                UInt256 amount = (ulong)shift;
                EvmInstructions.OpShr.Operation(in amount, in value, out UInt256 actual);
                UInt256 expected = UInt256.Zero;
                if (shift < 256) value.RightShift(shift, out expected);
                Assert.That(actual, Is.EqualTo(expected), $"{value} >> {shift}");
            }
        }
    }

    [Test]
    public void Shifts_write_the_slot_they_read()
    {
        foreach (UInt256 value in Values())
        {
            for (int shift = 0; shift < 256; shift += 7)
            {
                UInt256 amount = (ulong)shift;
                UInt256 slot = value;
                EvmInstructions.OpShl.Operation(in amount, in slot, out slot);
                value.LeftShift(shift, out UInt256 left);
                Assert.That(slot, Is.EqualTo(left), $"{value} << {shift} in place");

                slot = value;
                EvmInstructions.OpShr.Operation(in amount, in slot, out slot);
                value.RightShift(shift, out UInt256 right);
                Assert.That(slot, Is.EqualTo(right), $"{value} >> {shift} in place");
            }
        }
    }

    [Test]
    public void Amounts_above_64_bits_give_zero()
    {
        UInt256 amount = new(1, 1, 0, 0);
        EvmInstructions.OpShl.Operation(in amount, UInt256.MaxValue, out UInt256 left);
        EvmInstructions.OpShr.Operation(in amount, UInt256.MaxValue, out UInt256 right);
        Assert.That(left, Is.EqualTo(UInt256.Zero));
        Assert.That(right, Is.EqualTo(UInt256.Zero));
    }
}
