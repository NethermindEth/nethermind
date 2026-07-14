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
    public void Sustained_sweep_fires_after_interval_without_gen2_collections()
    {
        StabilizeAndDrain();

        for (int i = 1; i < GCScheduler.SustainedSweepBlockInterval; i++)
        {
            GCScheduler.Instance.NotifyBlockProcessed();
            Assert.That(GCScheduler.Instance.BlocksSinceSustainedSweep, Is.EqualTo(i));
        }

        GCScheduler.Instance.NotifyBlockProcessed();
        Assert.That(GCScheduler.Instance.BlocksSinceSustainedSweep, Is.Zero);
    }

    [Test]
    public void Sustained_sweep_is_postponed_when_gen2_was_collected_by_another_mechanism()
    {
        StabilizeAndDrain();

        for (int i = 0; i < 10; i++)
        {
            GCScheduler.Instance.NotifyBlockProcessed();
        }

        Assert.That(GCScheduler.Instance.BlocksSinceSustainedSweep, Is.EqualTo(10));

        // An idle-window sweep / runtime collection elsewhere: the accumulated interval restarts.
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: false);
        GCScheduler.Instance.NotifyBlockProcessed();
        Assert.That(GCScheduler.Instance.BlocksSinceSustainedSweep, Is.Zero);
    }

    [Test]
    public void Sustained_sweep_stays_armed_and_retries_while_gc_guard_is_held()
    {
        StabilizeAndDrain();

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

    // Force a real gen2 collection, then let the next notification observe it: the counter resets
    // and the gen2 observation is synced, giving each test a deterministic just-reset start.
    private static void StabilizeAndDrain()
    {
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: false);
        GCScheduler.Instance.NotifyBlockProcessed();
        Assert.That(GCScheduler.Instance.BlocksSinceSustainedSweep, Is.Zero);
    }
}
