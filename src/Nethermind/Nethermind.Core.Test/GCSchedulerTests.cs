// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core.Memory;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Core.Test;

// Tests use a private timer-less instance so the singleton's sweep timer cannot race assertions;
// the GC guard (MarkGCPaused) is still process-wide static state, hence NonParallelizable.
[NonParallelizable]
public class GCSchedulerTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);
    private readonly GCScheduler _scheduler = new(sustainedSweepEnabled: false);

    // Disarm the singleton's sweep so its timer cannot hold the shared static guard mid-test.
    [SetUp]
    public void SetUp() => GCScheduler.Instance.SweepBaselineAllocatedBytes = GC.GetTotalAllocatedBytes(precise: false);

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
    public void Collections_that_suspend_every_thread_are_refused_while_a_latency_sensitive_request_is_in_flight()
    {
        using (GCScheduler.EnterLatencySensitiveRequest(carriesBlock: false))
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(_scheduler.GCCollect(1, GCCollectionMode.Forced, blocking: true, compacting: false), Is.False);
                Assert.That(_scheduler.GCCollect(1, GCCollectionMode.Forced, blocking: false, compacting: false), Is.False, "a gen1 has no background form; the flag does not make it one");
                Assert.That(_scheduler.GCCollect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: false, compacting: true), Is.False, "compaction forces a blocking collection");
                Assert.That(_scheduler.GCCollect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: false, compacting: false), Is.True, "a background gen2 only pauses briefly");
            }
        }

        Assert.That(_scheduler.GCCollect(1, GCCollectionMode.Forced, blocking: true, compacting: false), Is.True, "the refusal outlived the request");
    }

    [Test]
    public void Registering_a_request_never_waits_for_a_collection_in_progress()
    {
        using ManualResetEventSlim collecting = new(false);
        using ManualResetEventSlim release = new(false);
        GCScheduler scheduler = new(sustainedSweepEnabled: false, collect: (_, _, _, _) =>
        {
            collecting.Set();
            release.Wait();
        });

        Task<bool> collection = Task.Run(() => scheduler.GCCollect(1, GCCollectionMode.Forced, blocking: true, compacting: false, trimNativeMemory: false));
        try
        {
            Assert.That(collecting.Wait(Patience), "the collection never started");
            // The runtime makes a blocking request wait for a running background GC; a request must not wait with it.
            Task registered = Task.Run(static () => GCScheduler.EnterLatencySensitiveRequest(carriesBlock: true).Dispose());
            Assert.That(registered.Wait(Patience), "registering a request waited for the collection in progress");
        }
        finally
        {
            release.Set();
        }

        Assert.That(collection.Result, Is.True);
    }

    [Test]
    public void Refused_collection_disarms_the_large_object_heap_compaction_its_caller_requested()
    {
        using (GCScheduler.EnterLatencySensitiveRequest(carriesBlock: false))
        {
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            Assert.That(_scheduler.GCCollect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true), Is.False);
        }

        Assert.That(GCSettings.LargeObjectHeapCompactionMode, Is.EqualTo(GCLargeObjectHeapCompactionMode.Default), "the runtime's next gen2 would have compacted the LOH inside a block");
    }

    [Test]
    public void Block_carrying_request_is_numbered_and_the_number_follows_its_async_flow()
    {
        long before = GCScheduler.LatencySensitiveRequests;
        using (GCScheduler.EnterLatencySensitiveRequest(carriesBlock: false))
        {
            Assert.That(GCScheduler.LatencySensitiveRequests, Is.EqualTo(before), "a call without a block is not an arrival");
        }

        long claimedInsideScope;
        using (GCScheduler.EnterLatencySensitiveRequest(carriesBlock: true))
        {
            claimedInsideScope = GCScheduler.ClaimLatencySensitiveRequest();
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(claimedInsideScope, Is.EqualTo(before + 1), "the claim must return the flow's own number, not a new one");
            Assert.That(GCScheduler.LatencySensitiveRequests, Is.EqualTo(before + 1));
        }
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
    public void Sweep_WhenAllocationBudgetExceeded_DoesNotTrimNativeHeap()
    {
        MallocHelper mallocHelper = Substitute.For<MallocHelper>();
        GCScheduler scheduler = new(sustainedSweepEnabled: false, mallocHelper);
        scheduler.SweepBaselineAllocatedBytes =
            GC.GetTotalAllocatedBytes(precise: false) - GCScheduler.SustainedSweepAllocationBytes - 1;

        scheduler.SweepIfAllocationBudgetExceeded();

        mallocHelper.DidNotReceive().MallocTrim(Arg.Any<uint>());
    }

    private long ArmBudget()
    {
        long armed = GC.GetTotalAllocatedBytes(precise: false) - GCScheduler.SustainedSweepAllocationBytes - 1;
        _scheduler.SweepBaselineAllocatedBytes = armed;
        return armed;
    }
}
