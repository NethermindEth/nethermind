// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Extensions;
using NUnit.Framework;

namespace Nethermind.Core.Test;

[TestFixture]
public class SizeExtensionsTests
{
    // Int64.MaxValue / 1_000_000_000: the largest long whose GB product still fits in a long.
    private const long MaxGbLong = 9_223_372_036L;

    [TestCase(0L, 0L)]
    [TestCase(1L, 1_000_000_000L)]
    [TestCase(1000L, 1_000_000_000_000L)]
    [TestCase(MaxGbLong, 9_223_372_036_000_000_000L)]
    public void GB_long_multiplies_exactly(long input, long expected) => Assert.That(input.GB, Is.EqualTo(expected));

    // GB multiplies unchecked. One past the boundary wraps negative; callers must range-check first.
    [Test]
    public void GB_long_wraps_past_boundary() => Assert.That((MaxGbLong + 1).GB, Is.EqualTo(-9_223_372_036_709_551_616L));

    // An int input cannot overflow: it widens to long before the multiply.
    [TestCase(0, 0L)]
    [TestCase(1000, 1_000_000_000_000L)]
    [TestCase(int.MaxValue, 2_147_483_647_000_000_000L)]
    public void GB_int_multiplies_exactly(int input, long expected) => Assert.That(input.GB, Is.EqualTo(expected));
}
