// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using NUnit.Framework;

namespace Nethermind.Core.Test;

// Exercises the process-wide GCScheduler singleton (shared GC-guard flag and sweep counter).
[NonParallelizable]
public class GCSchedulerTests
{
    [Test]
    public void Sustained_sweep_fires_after_interval_and_resets_counter()
    {
        DrainSweepCounter();

        for (int i = 1; i < GCScheduler.SustainedSweepBlockInterval; i++)
        {
            GCScheduler.Instance.NotifyBlockProcessed();
            Assert.That(GCScheduler.Instance.BlocksSinceSustainedSweep, Is.EqualTo(i));
        }

        GCScheduler.Instance.NotifyBlockProcessed();
        Assert.That(GCScheduler.Instance.BlocksSinceSustainedSweep, Is.Zero);
    }

    [Test]
    public void Sustained_sweep_is_postponed_by_scheduler_issued_gen2_but_not_gen1()
    {
        DrainSweepCounter();

        for (int i = 0; i < 10; i++)
        {
            GCScheduler.Instance.NotifyBlockProcessed();
        }

        Assert.That(GCScheduler.Instance.BlocksSinceSustainedSweep, Is.EqualTo(10));

        // A gen1 idle-window sweep does not collect gen2 — the interval keeps accumulating.
        Assert.That(GCScheduler.Instance.GCCollect(1, GCCollectionMode.Forced, blocking: false, compacting: false), Is.True);
        Assert.That(GCScheduler.Instance.BlocksSinceSustainedSweep, Is.EqualTo(10));

        // A gen2 idle-window sweep restarts it: gen2 was just collected by another mechanism.
        Assert.That(GCScheduler.Instance.GCCollect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: false, compacting: false), Is.True);
        Assert.That(GCScheduler.Instance.BlocksSinceSustainedSweep, Is.Zero);
    }

    [Test]
    public void Sustained_sweep_stays_armed_and_retries_while_gc_guard_is_held()
    {
        DrainSweepCounter();

        for (int i = 1; i < GCScheduler.SustainedSweepBlockInterval; i++)
        {
            GCScheduler.Instance.NotifyBlockProcessed();
        }

        Assert.That(GCScheduler.MarkGCPaused(), Is.True);
        try
        {
            // The interval elapses while the guard is held: no sweep, counter keeps advancing.
            GCScheduler.Instance.NotifyBlockProcessed();
            Assert.That(GCScheduler.Instance.BlocksSinceSustainedSweep, Is.EqualTo(GCScheduler.SustainedSweepBlockInterval));
            GCScheduler.Instance.NotifyBlockProcessed();
            Assert.That(GCScheduler.Instance.BlocksSinceSustainedSweep, Is.EqualTo(GCScheduler.SustainedSweepBlockInterval + 1));
        }
        finally
        {
            GCScheduler.MarkGCResumed();
        }

        // Guard released: the next block fires the retried sweep.
        GCScheduler.Instance.NotifyBlockProcessed();
        Assert.That(GCScheduler.Instance.BlocksSinceSustainedSweep, Is.Zero);
    }

    // The singleton counter survives across tests; a gen2 GCCollect resets it deterministically.
    private static void DrainSweepCounter()
    {
        Assert.That(GCScheduler.Instance.GCCollect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: false, compacting: false), Is.True);
        Assert.That(GCScheduler.Instance.BlocksSinceSustainedSweep, Is.Zero);
    }
}
