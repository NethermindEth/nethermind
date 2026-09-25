// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime;
using Nethermind.Core.Memory;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Core.Test;

// Tests use a private timer-less instance so the singleton's sweep timer cannot race assertions;
// the GC guard (MarkGCPaused) is still process-wide static state, hence NonParallelizable.
[NonParallelizable]
public class GCSchedulerTests
{
    private readonly GCScheduler _scheduler = new(sustainedSweepEnabled: false);

    // Disarm the singleton's sweep so its timer cannot hold the shared static guard mid-test.
    [SetUp]
    public void SetUp() => GCScheduler.Instance.SweepBaselineAllocatedBytes = GC.GetTotalAllocatedBytes(precise: false);

    [Test]
    public void Refused_collection_does_not_arm_loh_compaction(
        [Values(GCCollectionMode.Aggressive, GCCollectionMode.Forced)] GCCollectionMode mode)
    {
        bool paused = GCScheduler.MarkGCPaused();
        Assert.That(paused, Is.True);
        GCLargeObjectHeapCompactionMode previous = GCSettings.LargeObjectHeapCompactionMode;
        try
        {
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.Default;
            bool collected = _scheduler.GCCollect(GC.MaxGeneration, mode, blocking: true, compacting: true,
                trimNativeMemory: true, compactLoh: true);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(collected, Is.False);
                Assert.That(GCSettings.LargeObjectHeapCompactionMode, Is.EqualTo(GCLargeObjectHeapCompactionMode.Default));
            }
        }
        finally
        {
            GCSettings.LargeObjectHeapCompactionMode = previous;
            if (paused) GCScheduler.MarkGCResumed();
        }
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
    public void Sweep_stays_armed_while_guard_is_held_then_retries()
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

        _scheduler.SweepIfAllocationBudgetExceeded();
        Assert.That(_scheduler.SweepBaselineAllocatedBytes, Is.GreaterThan(armed));
    }

    [Test]
    public void Sweep_WhenAllocationBudgetExceeded_DoesNotTrimNativeHeap()
    {
        MallocHelper mallocHelper = Substitute.For<MallocHelper>();
        GCScheduler scheduler = new(sustainedSweepEnabled: false, mallocHelper);
        scheduler.SweepBaselineAllocatedBytes =
            GC.GetTotalAllocatedBytes(precise: false) - GCScheduler.SustainedSweepAllocationBytes - 1;

        scheduler.SweepIfAllocationBudgetExceeded();

        mallocHelper.DidNotReceive().MallocTrim(Arg.Any<uint>());
    }

    [Test]
    public void Idle_compaction_never_arms_loh_explicitly()
    {
        GCScheduler scheduler = new(sustainedSweepEnabled: false);
        scheduler.SetNextGcForTest(blocking: true, compacting: true);
        bool paused = GCScheduler.MarkGCPaused();
        Assert.That(paused, Is.True);
        GCLargeObjectHeapCompactionMode previous = GCSettings.LargeObjectHeapCompactionMode;
        try
        {
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.Default;
            scheduler.PerformFullGC();
            Assert.That(GCSettings.LargeObjectHeapCompactionMode, Is.EqualTo(GCLargeObjectHeapCompactionMode.Default));
        }
        finally
        {
            GCSettings.LargeObjectHeapCompactionMode = previous;
            if (paused) GCScheduler.MarkGCResumed();
        }
    }

    private long ArmBudget()
    {
        long armed = GC.GetTotalAllocatedBytes(precise: false) - GCScheduler.SustainedSweepAllocationBytes - 1;
        _scheduler.SweepBaselineAllocatedBytes = armed;
        return armed;
    }
}
