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

    // Disarm the singleton's sweep so its timer cannot hold the shared static guard mid-test,
    // and clear pending-sweep state left by a previous test on the shared fixture instance.
    [SetUp]
    public void SetUp()
    {
        GCScheduler.Instance.SweepBaselineAllocatedBytes = GC.GetTotalAllocatedBytes(precise: false);
        _scheduler.SetPendingSweep(-1, -1, 0);
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
    public void Sweep_stays_armed_while_issued_sweep_is_in_flight_then_fires_after_completion()
    {
        long armed = ArmBudget();
        _scheduler.SetPendingSweep(
            GC.GetGCMemoryInfo(GCKind.Background).Index,
            GC.GetGCMemoryInfo(GCKind.FullBlocking).Index,
            Environment.TickCount64);

        _scheduler.SweepIfAllocationBudgetExceeded();
        Assert.That(_scheduler.SweepBaselineAllocatedBytes, Is.EqualTo(armed));

        // A completed gen2 advances the FullBlocking index past the pending baseline.
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: false);

        _scheduler.SweepIfAllocationBudgetExceeded();
        Assert.That(_scheduler.SweepBaselineAllocatedBytes, Is.GreaterThan(armed));
    }

    [Test]
    public void Sweep_pending_state_expires_after_timeout()
    {
        long armed = ArmBudget();
        _scheduler.SetPendingSweep(
            GC.GetGCMemoryInfo(GCKind.Background).Index,
            GC.GetGCMemoryInfo(GCKind.FullBlocking).Index,
            Environment.TickCount64 - 61_000);

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
