// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using NUnit.Framework;

namespace Nethermind.Core.Test;

// Exercises the process-wide GCScheduler singleton (shared GC-guard flag and sweep counter).
[NonParallelizable]
public class GCSchedulerTests
{
    [Test]
    public void Sustained_sweep_fires_at_interval_and_resets_counter()
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

    // The singleton counter survives across tests; walk it to a just-swept state first.
    private static void DrainSweepCounter()
    {
        for (int i = 0; i <= GCScheduler.SustainedSweepBlockInterval && GCScheduler.Instance.BlocksSinceSustainedSweep != 0; i++)
        {
            GCScheduler.Instance.NotifyBlockProcessed();
        }

        Assert.That(GCScheduler.Instance.BlocksSinceSustainedSweep, Is.Zero, "sweep counter could not be drained");
    }
}
