// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Extensions;
using NUnit.Framework;

namespace Nethermind.Core.ZkEvm.Test.Extensions;

/// <summary>
/// Covers the guest forms of <see cref="Bytes.AreEqual32"/> and <see cref="Bytes.IsZero32"/>, which the
/// host suite cannot reach: the host build compiles the <see cref="System.Runtime.Intrinsics.Vector256{T}"/>
/// variants instead, so only a ZK_EVM build exercises the whole-word comparisons.
/// </summary>
/// <remarks>
/// Every byte and every bit gets its own case: the guest form folds the 32 bytes into four lanes, so a
/// swapped lane index or a dropped lane shows up only for the byte positions that land in it.
/// </remarks>
public class WordEqualityTests
{
    private static byte[] Pattern()
    {
        byte[] bytes = new byte[32];
        for (int i = 0; i < bytes.Length; i++) bytes[i] = (byte)(i * 7 + 1);
        return bytes;
    }

    [Test]
    public void Equal_spans_compare_equal()
    {
        byte[] a = Pattern();
        byte[] b = Pattern();

        Assert.That(Bytes.AreEqual32(ref a[0], ref b[0]), Is.True);
    }

    [TestCase(0)]
    [TestCase(1)]
    [TestCase(7)]
    [TestCase(8)]
    [TestCase(15)]
    [TestCase(16)]
    [TestCase(23)]
    [TestCase(24)]
    [TestCase(31)]
    public void A_difference_at_any_byte_compares_unequal(int index)
    {
        byte[] a = Pattern();
        byte[] b = Pattern();
        b[index] ^= 0x80;

        Assert.That(Bytes.AreEqual32(ref a[0], ref b[0]), Is.False);
    }

    [Test]
    public void Every_byte_and_bit_matches_a_reference_comparison()
    {
        byte[] a = Pattern();
        for (int index = 0; index < 32; index++)
        {
            for (int bit = 0; bit < 8; bit++)
            {
                byte[] b = Pattern();
                b[index] ^= (byte)(1 << bit);

                Assert.That(Bytes.AreEqual32(ref a[0], ref b[0]),
                    Is.EqualTo(((ReadOnlySpan<byte>)a).SequenceEqual(b)), $"byte {index}, bit {bit}");
            }
        }
    }

    [Test]
    public void An_all_zero_span_is_zero()
    {
        byte[] zero = new byte[32];

        Assert.That(Bytes.IsZero32(ref zero[0]), Is.True);
    }

    [Test]
    public void Every_single_set_bit_makes_a_span_non_zero()
    {
        for (int index = 0; index < 32; index++)
        {
            for (int bit = 0; bit < 8; bit++)
            {
                byte[] bytes = new byte[32];
                bytes[index] = (byte)(1 << bit);

                Assert.That(Bytes.IsZero32(ref bytes[0]), Is.False, $"byte {index}, bit {bit}");
            }
        }
    }

    [Test]
    public void Only_the_first_32_bytes_are_read()
    {
        byte[] a = new byte[40];
        byte[] b = new byte[40];
        a[32] = 0xFF;
        b[39] = 0xFF;

        Assert.That(Bytes.AreEqual32(ref a[0], ref b[0]), Is.True);
        Assert.That(Bytes.IsZero32(ref a[0]), Is.True);
    }
}
