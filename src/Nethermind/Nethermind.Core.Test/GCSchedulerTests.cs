// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using NUnit.Framework;

namespace Nethermind.Core.Test;

// Tests use a private timer-less instance so the singleton's sweep timer cannot race assertions;
// the GC guard (MarkGCPaused) is still process-wide static state, hence NonParallelizable.
[NonParallelizable]
public class GCSchedulerTests
{
    private readonly GCScheduler _scheduler = new(sustainedSweepEnabled: false);

    // Disarm the singleton's sweep and clear background-GC tracking state.
    [SetUp]
    public void SetUp()
    {
        GCScheduler.Instance.SweepBaselineAllocatedBytes = GC.GetTotalAllocatedBytes(precise: false);
        GCScheduler.BackgroundGCStartedAtMs = -1;
    }

    [Test]
    public void Sweep_fires_only_when_allocation_budget_is_exceeded()
    {
        long baseline = GC.GetTotalAllocatedBytes(precise: false);
        _scheduler.SweepBaselineAllocatedBytes = baseline;
        _scheduler.SweepIfAllocationBudgetExceeded();
        Assert.That(_scheduler.SweepBaselineAllocatedBytes, Is.EqualTo(baseline));

        long armed = ArmBudget();
        _scheduler.SweepIfAllocationBudgetExceeded();
        Assert.That(_scheduler.SweepBaselineAllocatedBytes, Is.GreaterThan(armed));
    }

    [Test]
    public void Scheduler_issued_gen2_resets_the_budget_but_gen1_does_not()
    {
        _scheduler.SweepBaselineAllocatedBytes = 0;

        Assert.That(_scheduler.GCCollect(1, GCCollectionMode.Forced, blocking: false, compacting: false), Is.True);
        Assert.That(_scheduler.SweepBaselineAllocatedBytes, Is.Zero);

        Assert.That(_scheduler.GCCollect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: false, compacting: false), Is.True);
        Assert.That(_scheduler.SweepBaselineAllocatedBytes, Is.GreaterThan(0));
    }

    [Test]
    public void Sweep_stays_armed_while_guard_or_exclusion_is_held_then_retries()
    {
        long armed = ArmBudget();

        Assert.That(GCScheduler.MarkGCPaused(), Is.True);
        try
        {
            _scheduler.SweepIfAllocationBudgetExceeded();
            Assert.That(_scheduler.SweepBaselineAllocatedBytes, Is.EqualTo(armed));
        }
        finally
        {
            GCScheduler.MarkGCResumed();
        }

        using (_scheduler.ExcludeForcedGC())
        using (_scheduler.ExcludeForcedGC())
        {
            _scheduler.SweepIfAllocationBudgetExceeded();
            Assert.That(_scheduler.SweepBaselineAllocatedBytes, Is.EqualTo(armed));
        }

        _scheduler.SweepIfAllocationBudgetExceeded();
        Assert.That(_scheduler.SweepBaselineAllocatedBytes, Is.GreaterThan(armed));
    }

    [Test]
    public void Sweep_stays_armed_while_background_gc_is_in_flight_then_fires_after_completion()
    {
        long armed = ArmBudget();
        GCScheduler.BackgroundGCStartedAtMs = Environment.TickCount64;

        _scheduler.SweepIfAllocationBudgetExceeded();
        Assert.That(_scheduler.SweepBaselineAllocatedBytes, Is.EqualTo(armed));

        GCScheduler.BackgroundGCStartedAtMs = -1;

        _scheduler.SweepIfAllocationBudgetExceeded();
        Assert.That(_scheduler.SweepBaselineAllocatedBytes, Is.GreaterThan(armed));
    }

    [Test]
    public void Sweep_in_flight_state_expires_after_timeout()
    {
        long armed = ArmBudget();
        GCScheduler.BackgroundGCStartedAtMs = Environment.TickCount64 - 301_000;

        _scheduler.SweepIfAllocationBudgetExceeded();
        Assert.That(_scheduler.SweepBaselineAllocatedBytes, Is.GreaterThan(armed));
    }

    private long ArmBudget()
    {
        long armed = GC.GetTotalAllocatedBytes(precise: false) - GCScheduler.SustainedSweepAllocationBytes - 1;
        _scheduler.SweepBaselineAllocatedBytes = armed;
        return armed;
    }
}
