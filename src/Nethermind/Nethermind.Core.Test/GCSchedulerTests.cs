// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime;
using NUnit.Framework;

namespace Nethermind.Core.Test;

// Tests use a private timer-less instance so the singleton's sweep timer cannot race assertions;
// the GC guard (MarkGCPaused) is still process-wide static state, hence NonParallelizable.
[NonParallelizable]
public class GCSchedulerTests
{
    private readonly GCScheduler _scheduler = new(sustainedSweepEnabled: false);

    // Disarm the singleton's sweep so its timer cannot hold the shared static guard mid-test,
    // and clear blackout state left by a previous test.
    [SetUp]
    public void SetUp()
    {
        GCScheduler.Instance.SweepBaselineAllocatedBytes = GC.GetTotalAllocatedBytes(precise: false);
        GCScheduler.NoGCRegionBlackoutUntilMs = 0;
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
    public void Sweep_restores_latency_mode_and_starts_region_blackout()
    {
        GCLatencyMode entryMode = GCSettings.LatencyMode;
        long armed = ArmBudget();

        _scheduler.SweepIfAllocationBudgetExceeded();

        Assert.That(_scheduler.SweepBaselineAllocatedBytes, Is.GreaterThan(armed));
        Assert.That(GCSettings.LatencyMode, Is.EqualTo(entryMode));
        Assert.That(GCScheduler.IsNoGCRegionBlackoutActive, Is.True);
    }

    [Test]
    public void Region_blackout_expires()
    {
        GCScheduler.NoGCRegionBlackoutUntilMs = Environment.TickCount64 - 1;
        Assert.That(GCScheduler.IsNoGCRegionBlackoutActive, Is.False);

        GCScheduler.NoGCRegionBlackoutUntilMs = Environment.TickCount64 + 10_000;
        Assert.That(GCScheduler.IsNoGCRegionBlackoutActive, Is.True);
    }

    [Test]
    public void Skipped_sweep_does_not_start_region_blackout()
    {
        GCScheduler.NoGCRegionBlackoutUntilMs = 0;
        long armed = ArmBudget();

        Assert.That(GCScheduler.MarkGCPaused(), Is.True);
        try
        {
            _scheduler.SweepIfAllocationBudgetExceeded();
        }
        finally
        {
            GCScheduler.MarkGCResumed();
        }

        Assert.That(_scheduler.SweepBaselineAllocatedBytes, Is.EqualTo(armed));
        Assert.That(GCScheduler.IsNoGCRegionBlackoutActive, Is.False);
    }

    private long ArmBudget()
    {
        long armed = GC.GetTotalAllocatedBytes(precise: false) - GCScheduler.SustainedSweepAllocationBytes - 1;
        _scheduler.SweepBaselineAllocatedBytes = armed;
        return armed;
    }
}
