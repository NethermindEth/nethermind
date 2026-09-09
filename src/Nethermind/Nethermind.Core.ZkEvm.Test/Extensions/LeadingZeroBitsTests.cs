// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Numerics;
using Nethermind.Core.Extensions;
using NUnit.Framework;

namespace Nethermind.Core.ZkEvm.Test.Extensions;

/// <summary>
/// Covers the guest form of <see cref="Bytes.LeadingZeroBits"/>, which the host suite cannot reach:
/// the host build is <see cref="BitOperations.LeadingZeroCount(ulong)"/> itself, so only a ZK_EVM
/// build exercises the byte switch plus in-byte comparison tree that replaces it.
/// </summary>
public class LeadingZeroBitsTests
{
    [Test]
    public void Counts_the_same_bits_as_a_clz([Range(0, 63)] int bit)
    {
        ulong value = 1UL << bit;
        Assert.That(Bytes.LeadingZeroBits(value), Is.EqualTo(BitOperations.LeadingZeroCount(value)));
    }

    [Test]
    public void Counts_from_the_most_significant_set_bit([Range(0, 63)] int bit)
    {
        // Every bit below the leading one is set too, so an in-byte boundary read the wrong way shows up.
        ulong value = (1UL << bit) | ((1UL << bit) - 1);
        Assert.That(Bytes.LeadingZeroBits(value), Is.EqualTo(BitOperations.LeadingZeroCount(value)));
    }

    [Test]
    public void An_empty_word_is_all_zero_bits()
        => Assert.That(Bytes.LeadingZeroBits(0), Is.EqualTo(sizeof(ulong) * 8));
}
