// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using NUnit.Framework;

namespace Nethermind.Core.Test.BlockAccessLists;

[TestFixture]
public class PrestateAwareIndexComparerTests
{
    [Test]
    public void Prestate_sorts_before_zero() =>
        Assert.That(PrestateAwareIndexComparer.Instance.Compare(Eip7928Constants.PrestateIndex, 0u), Is.LessThan(0));

    // Highest realistic real index is far below uint.MaxValue (= PrestateIndex sentinel).
    [Test]
    public void Prestate_sorts_before_max_real_index() =>
        Assert.That(PrestateAwareIndexComparer.Instance.Compare(Eip7928Constants.PrestateIndex, uint.MaxValue - 1), Is.LessThan(0));

    [Test]
    public void Real_indices_compare_normally()
    {
        Assert.That(PrestateAwareIndexComparer.Instance.Compare(0u, 1u), Is.LessThan(0));
        Assert.That(PrestateAwareIndexComparer.Instance.Compare(5u, 5u), Is.EqualTo(0));
        Assert.That(PrestateAwareIndexComparer.Instance.Compare(7u, 3u), Is.GreaterThan(0));
    }

    [Test]
    public void Two_prestates_are_equal() =>
        Assert.That(PrestateAwareIndexComparer.Instance.Compare(Eip7928Constants.PrestateIndex, Eip7928Constants.PrestateIndex), Is.EqualTo(0));

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
