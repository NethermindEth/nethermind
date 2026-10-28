// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Nethermind.State.Pbt.Image;
using NUnit.Framework;

namespace Nethermind.State.Pbt.Test;

public class TeeEnumeratorTests
{
    [Test]
    public async Task Follower_yields_every_item_after_the_leader_on_another_thread([Values(1, 4, 1000)] int capacity)
    {
        bool sourceDisposed = false;
        TeeEnumerator<int> leader = new(Source(10_000, failAt: null, () => sourceDisposed = true), capacity);
        IEnumerator<int> follower = leader.Tee();

        List<int>[] read = await Task.WhenAll(Task.Run(() => Drain(leader)), Task.Run(() => Drain(follower)));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(read[0], Is.EqualTo(Enumerable.Range(0, 10_000)));
            Assert.That(read[1], Is.EqualTo(Enumerable.Range(0, 10_000)));
            Assert.That(sourceDisposed, Is.True);
        }
    }

    [Test]
    public void Follower_waits_for_the_leader_which_waits_at_capacity_until_the_follower_reads_or_is_disposed()
    {
        TeeEnumerator<int> leader = new(Source(10, failAt: null, static () => { }), capacity: 2);
        IEnumerator<int> follower = leader.Tee();

        Task<bool> waitingFollower = Task.Run(follower.MoveNext);
        Assert.That(waitingFollower.Wait(TimeSpan.FromMilliseconds(100)), Is.False, "the leader has yielded nothing yet");
        Assert.That(leader.MoveNext() && leader.MoveNext(), Is.True);
        Assert.That(waitingFollower.Wait(TimeSpan.FromSeconds(10)) && waitingFollower.Result && follower.Current == 0, Is.True);

        Assert.That(leader.MoveNext(), Is.True);
        Task<bool> waitingLeader = Task.Run(leader.MoveNext);
        Assert.That(waitingLeader.Wait(TimeSpan.FromMilliseconds(100)), Is.False, "two items ahead of the follower");
        Assert.That(follower.MoveNext() && follower.Current == 1, Is.True);
        Assert.That(waitingLeader.Wait(TimeSpan.FromSeconds(10)) && waitingLeader.Result && leader.Current == 3, Is.True);

        follower.Dispose();
        Assert.That(Drain(leader), Is.EqualTo(Enumerable.Range(4, 6)), "a disposed follower no longer holds the leader back");
    }

    [TestCase(true, typeof(InvalidOperationException))]
    [TestCase(false, typeof(OperationCanceledException))]
    public void Follower_drains_what_the_leader_read_then_rethrows_its_failure_or_is_cancelled_by_its_disposal(bool sourceFails, Type expected)
    {
        TeeEnumerator<int> leader = new(Source(10, failAt: sourceFails ? 2 : null, static () => { }), capacity: 4);
        IEnumerator<int> follower = leader.Tee();
        Assert.That(leader.MoveNext() && leader.MoveNext(), Is.True);
        if (sourceFails) Assert.Throws<InvalidOperationException>(() => leader.MoveNext());
        else leader.Dispose();

        Assert.That(follower.MoveNext() && follower.Current == 0 && follower.MoveNext() && follower.Current == 1, Is.True);
        Assert.Throws(expected, () => follower.MoveNext());
    }

    private static List<int> Drain(IEnumerator<int> enumerator)
    {
        List<int> items = [];
        using (enumerator)
        {
            while (enumerator.MoveNext()) items.Add(enumerator.Current);
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
