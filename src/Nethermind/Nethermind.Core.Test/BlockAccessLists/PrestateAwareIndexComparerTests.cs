// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core.BlockAccessLists;
using NUnit.Framework;

namespace Nethermind.Core.Test.BlockAccessLists;

[TestFixture]
public class PrestateAwareIndexComparerTests
{
    // Highest realistic real index is far below uint.MaxValue (= PrestateIndex sentinel).
    [TestCase(Eip7928Constants.PrestateIndex, 0u, -1, TestName = "Prestate_lt_zero")]
    [TestCase(Eip7928Constants.PrestateIndex, uint.MaxValue - 1, -1, TestName = "Prestate_lt_max_real")]
    [TestCase(Eip7928Constants.PrestateIndex, Eip7928Constants.PrestateIndex, 0, TestName = "Prestate_eq_prestate")]
    [TestCase(0u, 1u, -1, TestName = "Real_0_lt_1")]
    [TestCase(5u, 5u, 0, TestName = "Real_5_eq_5")]
    [TestCase(7u, 3u, 1, TestName = "Real_7_gt_3")]
    public void Comparer_orders_prestate_first(uint a, uint b, int expectedSign)
    {
        int actual = PrestateAwareIndexComparer.Instance.Compare(a, b);
        Assert.That(Math.Sign(actual), Is.EqualTo(expectedSign));
    }

    [Test]
    public void SortedList_iterates_prestate_first()
    {
        // Ensures the legacy "-1 sorts first" semantics are preserved with uint sentinel.
        SortedList<uint, string> list = new(PrestateAwareIndexComparer.Instance)
        {
            { 5u, "five" },
            { Eip7928Constants.PrestateIndex, "prestate" },
            { 0u, "zero" },
            { 2u, "two" },
        };

        List<KeyValuePair<uint, string>> ordered = [.. list];

        Assert.That(ordered[0].Value, Is.EqualTo("prestate"));
        Assert.That(ordered[1].Value, Is.EqualTo("zero"));
        Assert.That(ordered[2].Value, Is.EqualTo("two"));
        Assert.That(ordered[3].Value, Is.EqualTo("five"));
    }

    [Test]
    public void SortedList_last_is_highest_real_when_prestate_present()
    {
        // ApplyStateChanges uses [^1].Index != PrestateIndex to gate writes;
        // verifies the last entry remains the highest real index.
        SortedList<uint, int> list = new(PrestateAwareIndexComparer.Instance)
        {
            { Eip7928Constants.PrestateIndex, -1 },
            { 0u, 0 },
            { 3u, 3 },
        };

        Assert.That(list.Keys[^1], Is.EqualTo(3u));
    }

    [Test]
    public void SortedList_last_is_prestate_when_only_prestate_present()
    {
        SortedList<uint, int> list = new(PrestateAwareIndexComparer.Instance)
        {
            { Eip7928Constants.PrestateIndex, -1 },
        };

        Assert.That(list.Keys[^1], Is.EqualTo(Eip7928Constants.PrestateIndex));
    }
}
