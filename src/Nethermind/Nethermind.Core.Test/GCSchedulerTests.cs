// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using NUnit.Framework;

namespace Nethermind.Core.Test;

[NonParallelizable]
public class GCSchedulerTests
{
    [Test]
    public void Sweep_fires_only_when_allocation_budget_is_exceeded()
    {
        long baseline = GC.GetTotalAllocatedBytes(precise: false);
        GCScheduler.Instance.SweepBaselineAllocatedBytes = baseline;
        GCScheduler.Instance.SweepIfAllocationBudgetExceeded();
        Assert.That(GCScheduler.Instance.SweepBaselineAllocatedBytes, Is.EqualTo(baseline));

        long armed = ArmBudget();
        GCScheduler.Instance.SweepIfAllocationBudgetExceeded();
        Assert.That(GCScheduler.Instance.SweepBaselineAllocatedBytes, Is.GreaterThan(armed));
    }

    [Test]
    public void Scheduler_issued_gen2_resets_the_budget_but_gen1_does_not()
    {
        GCScheduler.Instance.SweepBaselineAllocatedBytes = 0;

        Assert.That(GCScheduler.Instance.GCCollect(1, GCCollectionMode.Forced, blocking: false, compacting: false), Is.True);
        Assert.That(GCScheduler.Instance.SweepBaselineAllocatedBytes, Is.Zero);

        Assert.That(GCScheduler.Instance.GCCollect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: false, compacting: false), Is.True);
        Assert.That(GCScheduler.Instance.SweepBaselineAllocatedBytes, Is.GreaterThan(0));
    }

    [Test]
    public void Sweep_stays_armed_while_guard_or_exclusion_is_held_then_retries()
    {
        long armed = ArmBudget();

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

        using (GCScheduler.Instance.ExcludeForcedGC())
        using (GCScheduler.Instance.ExcludeForcedGC())
        {
            GCScheduler.Instance.SweepIfAllocationBudgetExceeded();
            Assert.That(GCScheduler.Instance.SweepBaselineAllocatedBytes, Is.EqualTo(armed));
        }

        GCScheduler.Instance.SweepIfAllocationBudgetExceeded();
        Assert.That(GCScheduler.Instance.SweepBaselineAllocatedBytes, Is.GreaterThan(armed));
    }

    private static long ArmBudget()
    {
        long armed = GC.GetTotalAllocatedBytes(precise: false) - GCScheduler.SustainedSweepAllocationBytes - 1;
        GCScheduler.Instance.SweepBaselineAllocatedBytes = armed;
        return armed;
    }
}
