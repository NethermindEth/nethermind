// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test;

public class SweepPacerTests
{
    [Test]
    public void A_sweep_that_asks_again_the_moment_its_pass_ends_does_not_starve_the_other_one()
    {
        SweepPacer pacer = new();
        List<string> order = [];
        object sync = new();
        using ManualResetEventSlim greedyHoldsTheTurn = new();
        using ManualResetEventSlim otherIsWaiting = new();

        Task greedy = Task.Run(() =>
        {
            for (int i = 0; i < 40; i++)
            {
                pacer.Run(() =>
                {
                    lock (sync) order.Add("greedy");
                    if (i == 0)
                    {
                        greedyHoldsTheTurn.Set();
                        otherIsWaiting.Wait();
                        Thread.Sleep(20);
                    }
                    return false;
                }, CancellationToken.None);
            }
            return true;
        });

        greedyHoldsTheTurn.Wait();
        Task other = Task.Run(() =>
        {
            otherIsWaiting.Set();
            return pacer.Run(() =>
            {
                lock (sync) order.Add("other");
                return true;
            }, CancellationToken.None);
        });

        Assert.That(Task.WaitAll([greedy, other], TimeSpan.FromSeconds(30)), Is.True);
        int position;
        lock (sync) position = order.IndexOf("other");
        Assert.That(position, Is.EqualTo(1), "the other sweep asked while the first pass was running, so it owns the very next turn; a semaphore lets the greedy loop re-enter first and the other sweep never runs");
    }

    [Test]
    public void A_waiter_that_is_cancelled_gives_up_its_place_and_the_line_keeps_moving()
    {
        SweepPacer pacer = new();
        using ManualResetEventSlim holding = new();
        using ManualResetEventSlim release = new();
        using CancellationTokenSource cancelled = new();

        Task first = Task.Run(() => pacer.Run(() => { holding.Set(); release.Wait(); return true; }, CancellationToken.None));
        holding.Wait();
        Task second = Task.Run(() => pacer.Run(() => true, cancelled.Token));
        Thread.Sleep(50);
        cancelled.Cancel();

        Assert.That(() => second.GetAwaiter().GetResult(), Throws.InstanceOf<OperationCanceledException>());
        Task third = Task.Run(() => pacer.Run(() => true, CancellationToken.None));
        release.Set();

        Assert.That(Task.WaitAll([first, third], TimeSpan.FromSeconds(10)), Is.True, "the cancelled waiter's ticket is skipped, so the turn after the holder goes to the next live waiter");
    }
}
