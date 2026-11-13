// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;

namespace Nethermind.LightClient.Test;

public class VerifiedHeadTests
{
    [TestCase(100UL, 99UL, 10UL, false)]
    [TestCase(100UL, 100UL, 0UL, true)]
    [TestCase(100UL, 101UL, 0UL, false)]
    [TestCase(100UL, 110UL, 10UL, true)]
    [TestCase(100UL, 111UL, 10UL, false)]
    [TestCase(ulong.MaxValue, ulong.MaxValue, 0UL, true)]
    [TestCase(ulong.MaxValue - 5, ulong.MaxValue, 10UL, true)]
    [TestCase(ulong.MaxValue - 11, ulong.MaxValue, 10UL, false)]
    [TestCase(0UL, ulong.MaxValue, 10UL, false)]
    [TestCase(0UL, ulong.MaxValue, ulong.MaxValue, true)]
    [TestCase(ulong.MaxValue, 0UL, ulong.MaxValue, false)]
    public void Freshness_is_bounded_without_unsigned_wraparound(ulong headSlot, ulong currentSlot, ulong maximumAgeSlots, bool expected)
    {
        VerifiedHead head = new(headSlot, 10, Hash256.Zero, Hash256.Zero);

        Assert.That(head.IsFresh(currentSlot, maximumAgeSlots), Is.EqualTo(expected));
    }
}
