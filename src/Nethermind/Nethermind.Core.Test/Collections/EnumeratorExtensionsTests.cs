// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Nethermind.Core.Collections;
using NUnit.Framework;

namespace Nethermind.Core.Test.Collections;

public class EnumeratorExtensionsTests
{
    [Test]
    public async Task Teed_yields_every_item_to_both_branches_on_separate_threads_then_disposes_the_source([Values(1, 4, 1000)] int capacity)
    {
        bool sourceDisposed = false;
        (IEnumerator<int> first, IEnumerator<int> second) = Source(10_000, failAt: null, () => sourceDisposed = true).Teed(capacity);

        List<int>[] read = await Task.WhenAll(Task.Run(() => Drain(first)), Task.Run(() => Drain(second)));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(read[0], Is.EqualTo(Enumerable.Range(0, 10_000)));
            Assert.That(read[1], Is.EqualTo(Enumerable.Range(0, 10_000)));
            Assert.That(sourceDisposed, Is.True);
        }
    }

    [Test]
    public void Teed_holds_the_leading_branch_at_capacity_until_the_other_reads_or_is_disposed()
    {
        (IEnumerator<int> first, IEnumerator<int> second) = Source(10, failAt: null, static () => { }).Teed(capacity: 2);
        Assert.That(first.MoveNext() && first.MoveNext(), Is.True);

        Task<bool> blocked = Task.Run(first.MoveNext);
        Assert.That(blocked.Wait(TimeSpan.FromMilliseconds(100)), Is.False, "two items ahead of the other branch");
        Assert.That(second.MoveNext() && second.Current == 0, Is.True);
        Assert.That(blocked.Wait(TimeSpan.FromSeconds(10)) && blocked.Result && first.Current == 2, Is.True);

        second.Dispose();
        Assert.That(Drain(first), Is.EqualTo(Enumerable.Range(3, 7)), "a disposed branch no longer holds the other back");
    }

    [Test]
    public void Teed_rethrows_a_source_failure_on_each_branch_after_what_was_read_before_it()
    {
        (IEnumerator<int> first, IEnumerator<int> second) = Source(10, failAt: 2, static () => { }).Teed(capacity: 4);

        foreach (IEnumerator<int> branch in new[] { first, second })
        {
            Assert.That(branch.MoveNext() && branch.Current == 0 && branch.MoveNext() && branch.Current == 1, Is.True);
            Assert.Throws<InvalidOperationException>(() => branch.MoveNext());
        }
    }

    private static List<int> Drain(IEnumerator<int> branch)
    {
        List<int> items = [];
        using (branch)
        {
            while (branch.MoveNext()) items.Add(branch.Current);
        }
        return items;
    }

    private static IEnumerator<int> Source(int count, int? failAt, Action onDisposed)
    {
        try
        {
            for (int i = 0; i < count; i++)
            {
                if (i == failAt) throw new InvalidOperationException("source failed");
                yield return i;
            }
        }
        finally
        {
            onDisposed();
        }
    }
}
