// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using NUnit.Framework;

namespace Nethermind.Core.Test;

// Exercises the process-wide GCScheduler singleton (shared GC-guard flag and sweep baseline).
[NonParallelizable]
public class GCSchedulerTests
{
    [Test]
    public void Sustained_sweep_fires_once_allocation_budget_is_exceeded()
    {
        long before = GC.GetTotalAllocatedBytes(precise: false);
        GCScheduler.Instance.SweepBaselineAllocatedBytes = before - GCScheduler.SustainedSweepAllocationBytes - 1;

        GCScheduler.Instance.SweepIfAllocationBudgetExceeded();

        Assert.That(GCScheduler.Instance.SweepBaselineAllocatedBytes, Is.GreaterThanOrEqualTo(before));
    }

    [Test]
    public void Sustained_sweep_does_not_fire_below_allocation_budget()
    {
        long baseline = GC.GetTotalAllocatedBytes(precise: false);
        GCScheduler.Instance.SweepBaselineAllocatedBytes = baseline;

        GCScheduler.Instance.SweepIfAllocationBudgetExceeded();

        Assert.That(GCScheduler.Instance.SweepBaselineAllocatedBytes, Is.EqualTo(baseline));
    }

    [Test]
    public void Scheduler_issued_gen2_restarts_the_budget_but_gen1_does_not()
    {
        GCScheduler.Instance.SweepBaselineAllocatedBytes = 0;

        Assert.That(GCScheduler.Instance.GCCollect(1, GCCollectionMode.Forced, blocking: false, compacting: false), Is.True);
        Assert.That(GCScheduler.Instance.SweepBaselineAllocatedBytes, Is.Zero);

        long before = GC.GetTotalAllocatedBytes(precise: false);
        Assert.That(GCScheduler.Instance.GCCollect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: false, compacting: false), Is.True);
        Assert.That(GCScheduler.Instance.SweepBaselineAllocatedBytes, Is.GreaterThanOrEqualTo(before));
    }

    [Test]
    public void Sustained_sweep_stays_armed_and_retries_while_forced_gc_is_excluded()
    {
        long armed = GC.GetTotalAllocatedBytes(precise: false) - GCScheduler.SustainedSweepAllocationBytes - 1;
        GCScheduler.Instance.SweepBaselineAllocatedBytes = armed;

        using (GCScheduler.Instance.ExcludeForcedGC())
        {
            GCScheduler.Instance.SweepIfAllocationBudgetExceeded();
            Assert.That(GCScheduler.Instance.SweepBaselineAllocatedBytes, Is.EqualTo(armed));
            Assert.That(GCScheduler.Instance.GCCollect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: false, compacting: false), Is.False);
        }

        GCScheduler.Instance.SweepIfAllocationBudgetExceeded();
        Assert.That(GCScheduler.Instance.SweepBaselineAllocatedBytes, Is.GreaterThan(armed));
    }

    [Test]
    public void Forced_gc_exclusion_scopes_nest()
    {
        using (GCScheduler.Instance.ExcludeForcedGC())
        using (GCScheduler.Instance.ExcludeForcedGC())
        {
        }

        Assert.That(GCScheduler.Instance.GCCollect(1, GCCollectionMode.Forced, blocking: false, compacting: false), Is.True);
    }

    [Test]
    public void Sustained_sweep_stays_armed_and_retries_while_gc_guard_is_held()
    {
        long armed = GC.GetTotalAllocatedBytes(precise: false) - GCScheduler.SustainedSweepAllocationBytes - 1;
        GCScheduler.Instance.SweepBaselineAllocatedBytes = armed;

        Assert.That(GCScheduler.MarkGCPaused(), Is.True);
        try
        {
            GCScheduler.Instance.SweepIfAllocationBudgetExceeded();
            Assert.That(GCScheduler.Instance.SweepBaselineAllocatedBytes, Is.EqualTo(armed));
        }
        finally
        {
            GCScheduler.MarkGCResumed();
        }

        GCScheduler.Instance.SweepIfAllocationBudgetExceeded();
        Assert.That(GCScheduler.Instance.SweepBaselineAllocatedBytes, Is.GreaterThan(armed));
    }
}
