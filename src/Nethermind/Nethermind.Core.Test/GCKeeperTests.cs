// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core.Memory;
using Nethermind.Logging;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Core.Test;

[NonParallelizable]
public class GCKeeperTests
{
    [Test]
    public async Task Stop_waits_for_queued_entry([Values] bool releaseBeforeStop)
    {
        RegionRuntime runtime = new();
        List<IThreadPoolWorkItem> queued = [];
        using GCKeeper keeper = CreateRegionKeeper(runtime, queued.Add);
        using IDisposable lease = keeper.TryStartNoGCRegion();
        if (releaseBeforeStop) lease.Dispose();

        Task stop = keeper.StopAsync();
        Assert.That(stop.IsCompleted, Is.False);
        queued[0].Execute();
        await stop.WaitAsync(TimeSpan.FromSeconds(5));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(runtime.Starts, Is.Zero);
            Assert.That(runtime.IsActive, Is.False);
        }
    }

    [Test]
    public async Task Stop_waits_for_running_entry()
    {
        using ManualResetEventSlim entering = new(false);
        using ManualResetEventSlim proceed = new(false);
        RegionRuntime runtime = new() { BeforeStart = () => { entering.Set(); proceed.Wait(); } };
        List<IThreadPoolWorkItem> queued = [];
        using GCKeeper keeper = CreateRegionKeeper(runtime, queued.Add);
        using IDisposable lease = keeper.TryStartNoGCRegion();
        await AssertStopWaitsUntilReleased(keeper, entering, proceed, Task.Run(queued[0].Execute));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(runtime.Ends, Is.EqualTo(1));
            Assert.That(runtime.IsActive, Is.False);
        }
    }

    [Test]
    public async Task Stop_waits_for_region_release()
    {
        using ManualResetEventSlim ending = new(false);
        using ManualResetEventSlim proceed = new(false);
        RegionRuntime runtime = new() { BeforeEnd = () => { ending.Set(); proceed.Wait(); } };
        List<IThreadPoolWorkItem> queued = [];
        using GCKeeper keeper = CreateRegionKeeper(runtime, queued.Add);
        using IDisposable lease = keeper.TryStartNoGCRegion();
        queued[0].Execute();
        await AssertStopWaitsUntilReleased(keeper, ending, proceed, Task.Run(lease.Dispose));
        Assert.That(runtime.IsActive, Is.False);
    }

    [Test]
    public void Released_before_dispatch_skips_entry([Values] bool shutdown)
    {
        List<IThreadPoolWorkItem> queued = [];
        RegionRuntime runtime = new();
        using GCKeeper keeper = CreateRegionKeeper(runtime, queued.Add);
        using IDisposable lease = keeper.TryStartNoGCRegion();
        if (shutdown) keeper.Dispose();
        else lease.Dispose();
        if (!shutdown)
        {
            using IDisposable next = keeper.TryStartNoGCRegion();
            Assert.That(queued, Has.Count.EqualTo(2));
        }
        queued[0].Execute();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(runtime.Starts, Is.Zero);
            Assert.That(runtime.Ends, Is.Zero);
        }
    }

    [Test]
    public async Task Entry_returns_worker_before_payload_finishes()
    {
        List<IThreadPoolWorkItem> queued = [];
        RegionRuntime runtime = new();
        using GCKeeper keeper = CreateRegionKeeper(runtime, queued.Add);
        using IDisposable lease = keeper.TryStartNoGCRegion();
        await Task.Run(queued[0].Execute).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(runtime.IsActive, Is.True);
        lease.Dispose();
        lease.Dispose();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(runtime.IsActive, Is.False);
            Assert.That(runtime.Ends, Is.EqualTo(1));
        }
        using IDisposable next = keeper.TryStartNoGCRegion();
        Assert.That(queued, Has.Count.EqualTo(2));
    }

    [Test]
    public async Task Blocked_entry_does_not_hold_up_release_or_queue_more_workers([Values] bool shutdown)
    {
        using ManualResetEventSlim entering = new(false);
        using ManualResetEventSlim proceed = new(false);
        RegionRuntime runtime = new() { BeforeStart = () => { entering.Set(); proceed.Wait(); } };
        List<IThreadPoolWorkItem> queued = [];
        IGCStrategy strategy = Substitute.For<IGCStrategy>();
        strategy.CanStartNoGCRegion().Returns(true);
        strategy.GetForcedGCParams().Returns((GcLevel.NoGC, GcCompaction.No));
        using GCKeeper keeper = new(strategy, NullLogManager.Instance, runtime, queued.Add);
        using IDisposable lease = keeper.TryStartNoGCRegion();
        Task worker = Task.Run(queued[0].Execute);
        Task release = Task.CompletedTask;
        try
        {
            Assert.That(entering.Wait(TimeSpan.FromSeconds(5)), Is.True);
            release = Task.Run(() =>
            {
                if (shutdown) keeper.Dispose();
                else lease.Dispose();
            });
            await release.WaitAsync(TimeSpan.FromSeconds(5));
            strategy.Received(shutdown ? 0 : 1).GetForcedGCParams();
            using IDisposable next = keeper.TryStartNoGCRegion();
            Assert.That(queued, Has.Count.EqualTo(1));
        }
        finally
        {
            proceed.Set();
            await Task.WhenAll(worker, release).WaitAsync(TimeSpan.FromSeconds(5));
        }
        using (Assert.EnterMultipleScope())
        {
            Assert.That(runtime.Starts, Is.EqualTo(1));
            Assert.That(runtime.Ends, Is.EqualTo(1));
            Assert.That(runtime.IsActive, Is.False);
        }
    }

    [Test]
    public void Failed_entry_releases_the_slot_without_ending_another_region([Values] bool throws)
    {
        List<IThreadPoolWorkItem> queued = [];
        RegionRuntime runtime = new() { Refuse = true, Throw = throws };
        using GCKeeper keeper = CreateRegionKeeper(runtime, queued.Add);
        using (keeper.TryStartNoGCRegion()) queued[0].Execute();
        using (keeper.TryStartNoGCRegion()) queued[1].Execute();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(runtime.Starts, Is.EqualTo(2));
            Assert.That(runtime.Ends, Is.Zero);
        }
    }

    [TestCase(false, false, false, TestName = "{m}(entered)")]
    [TestCase(true, false, false, TestName = "{m}(declined)")]
    [TestCase(true, true, false, TestName = "{m}(threw)")]
    [TestCase(false, false, true, TestName = "{m}(payload ended first)")]
    public void Entries_are_counted_only_when_the_runtime_enters(bool refuse, bool throws, bool releaseFirst)
    {
        List<IThreadPoolWorkItem> queued = [];
        RegionRuntime runtime = new() { Refuse = refuse, Throw = throws };
        using GCKeeper keeper = CreateRegionKeeper(runtime, queued.Add);
        long entries = Interlocked.Read(ref Metrics.NoGcRegionEntries);
        long skips = Interlocked.Read(ref Metrics.NoGcRegionSkips);

        IDisposable lease = keeper.TryStartNoGCRegion();
        Assert.That(Interlocked.Read(ref Metrics.NoGcRegionEntries) - entries, Is.Zero, "queued, not yet entered");
        if (releaseFirst) lease.Dispose();
        queued[0].Execute();
        lease.Dispose();

        bool entered = !refuse && !releaseFirst;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(runtime.Starts, Is.EqualTo(releaseFirst ? 0 : 1));
            Assert.That(Interlocked.Read(ref Metrics.NoGcRegionEntries) - entries, Is.EqualTo(entered ? 1 : 0));
            Assert.That(Interlocked.Read(ref Metrics.NoGcRegionSkips) - skips, Is.EqualTo(entered ? 0 : 1));
        }
    }

    [Test]
    public void Guard_warns_once_when_the_runtime_does_not_expose_the_budget([Values] NoGcRegionMode mode, [Values] bool readable)
    {
        InterfaceLogger logger = Substitute.For<InterfaceLogger>();
        logger.IsWarn.Returns(true);
        // A budget the runtime exposes can read 0, which is not a reason to warn.
        RegionRuntime runtime = new() { CanReadGen0Budget = readable, Gen0Budget = readable ? 0 : -1 };
        using GCKeeper keeper = new(ModeStrategy(mode), new OneLoggerLogManager(new ILogger(logger)), runtime, static _ => { });
        for (int i = 0; i < 3; i++) keeper.TryStartNoGCRegion().Dispose();
        logger.Received(mode == NoGcRegionMode.Guard && !readable ? 1 : 0).Warn(Arg.Is<string>(message => message.StartsWith("No-GC region guard unavailable")));
    }

    [Test]
    public void Runtime_exposes_the_gen0_budget()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(GcRegionRuntime.Instance.CanReadGen0Budget, Is.True, "the guard would enter the region on every payload");
            Assert.That(GcRegionRuntime.Instance.Gen0Budget, Is.Positive);
        }
    }

    [Test]
    public void Ending_a_region_logs_expected_failure_at_debug([Values] bool expected)
    {
        InterfaceLogger logger = Substitute.For<InterfaceLogger>();
        logger.IsDebug.Returns(true);
        logger.IsError.Returns(true);
        Exception failure = expected ? new InvalidOperationException("Region ended.") : new Exception("Unexpected failure.");
        RegionRuntime runtime = new() { EndFailure = failure };
        IGCStrategy strategy = Substitute.For<IGCStrategy>();
        strategy.CanStartNoGCRegion().Returns(true);
        List<IThreadPoolWorkItem> queued = [];
        using GCKeeper keeper = new(strategy, new OneLoggerLogManager(new ILogger(logger)), runtime, queued.Add);
        using (keeper.TryStartNoGCRegion()) queued[0].Execute();
        logger.Received(expected ? 0 : 1).Error("No-GC region cleanup failed.", failure);
        logger.Received(expected ? 1 : 0).Debug(Arg.Is<string>(message => message.StartsWith("No-GC region already ended:")));
        using IDisposable next = keeper.TryStartNoGCRegion();
        Assert.That(queued, Has.Count.EqualTo(2));
    }

    [Test]
    public async Task Decommit_counts_payloads_with_cancelled_collections_and_retries_until_collected()
    {
        IGCStrategy strategy = Substitute.For<IGCStrategy>();
        strategy.CanStartNoGCRegion().Returns(true);
        strategy.GetForcedGCParams().Returns((GcLevel.Gen1, GcCompaction.No));
        strategy.CollectionsPerDecommit.Returns(50);
        RegionRuntime runtime = new();
        using GCKeeper keeper = new(strategy, NullLogManager.Instance, runtime, static _ => { }, static (_, _) => Task.FromResult(true));
        for (int i = 0; i < 50; i++)
        {
            CountPayload(keeper, strategy);
            await CancelCollectionAfterYield(keeper);
        }
        Assert.That(runtime.Collections, Is.Empty);

        runtime.CollectionSucceeds = false;
        await keeper.ScheduleGCInternal(throttle: false);
        runtime.CollectionSucceeds = true;
        await keeper.ScheduleGCInternal(throttle: false);
        await keeper.ScheduleGCInternal(throttle: false);
        Assert.That(runtime.Collections, Is.EqualTo(new[]
        {
            (GcLevel.Gen2, GCCollectionMode.Aggressive, GcCompaction.Full),
            (GcLevel.Gen2, GCCollectionMode.Aggressive, GcCompaction.Full),
            (GcLevel.Gen1, GCCollectionMode.Forced, GcCompaction.No)
        }));
    }

    [Test]
    public async Task Decommit_interval_preserves_sentinels([Values(-1, 0, 50)] int interval, [Values] NoGcRegionMode mode)
    {
        IGCStrategy strategy = Substitute.For<IGCStrategy>();
        strategy.CanStartNoGCRegion().Returns(true);
        strategy.NoGCRegionMode.Returns(mode);
        strategy.GetForcedGCParams().Returns((GcLevel.Gen1, GcCompaction.No));
        strategy.CollectionsPerDecommit.Returns(interval);
        RegionRuntime runtime = new();
        using GCKeeper keeper = new(strategy, NullLogManager.Instance, runtime, static _ => { }, static (_, _) => Task.FromResult(true));
        for (int i = 1; i <= 100; i++)
        {
            CountPayload(keeper, strategy);
            await keeper.ScheduleGCInternal(throttle: false);
            bool decommit = interval == 0 || (interval > 0 && i % interval == 0);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(runtime.Collections, Has.Count.EqualTo(i));
                Assert.That(runtime.Collections[^1], Is.EqualTo(decommit
                    ? (GcLevel.Gen2, GCCollectionMode.Aggressive, GcCompaction.Full)
                    : (GcLevel.Gen1, GCCollectionMode.Forced, GcCompaction.No)), $"Payload {i}");
            }
        }
    }

    [Test]
    public async Task Decommit_requires_a_longer_idle_gap([Values(0, 1500, 4000)] int postBlockDelayMs, [Values] bool decommit)
    {
        IGCStrategy strategy = Substitute.For<IGCStrategy>();
        strategy.PostBlockDelayMs.Returns(postBlockDelayMs);
        strategy.GetForcedGCParams().Returns((GcLevel.Gen1, GcCompaction.No));
        strategy.CollectionsPerDecommit.Returns(decommit ? 0 : -1);
        RegionRuntime runtime = new();
        List<int> delays = [];
        using GCKeeper keeper = new(strategy, NullLogManager.Instance, runtime, delay: (milliseconds, _) =>
        {
            delays.Add(milliseconds);
            return Task.FromResult(true);
        });
        await keeper.ScheduleGCInternal(throttle: false);
        int[] expected = decommit && postBlockDelayMs < 3000
            ? postBlockDelayMs == 0 ? [3000] : [postBlockDelayMs, 3000 - postBlockDelayMs]
            : postBlockDelayMs == 0 ? [] : [postBlockDelayMs];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(delays, Is.EqualTo(expected));
            Assert.That(runtime.Collections, Has.Count.EqualTo(1));
            Assert.That(runtime.Collections[0].Item2, Is.EqualTo(decommit ? GCCollectionMode.Aggressive : GCCollectionMode.Forced));
        }
    }

    [Test]
    public async Task Cancelled_idle_wait_retains_decommit_until_a_quiet_gap([Values] bool shutdown, [Values(1, 30)] int cancellations)
    {
        IGCStrategy strategy = Substitute.For<IGCStrategy>();
        strategy.CanStartNoGCRegion().Returns(true);
        strategy.CollectionsPerDecommit.Returns(1);
        RegionRuntime runtime = new();
        TaskCompletionSource waiting = new(TaskCreationOptions.RunContinuationsAsynchronously);
        bool wait = true;
        async Task<bool> Delay(int milliseconds, CancellationToken token)
        {
            if (!wait) return true;
            waiting.TrySetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, token);
                return true;
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }
        using GCKeeper keeper = new(strategy, NullLogManager.Instance, runtime, static _ => { }, Delay);
        for (int i = 0; i < cancellations; i++)
        {
            waiting = new(TaskCreationOptions.RunContinuationsAsynchronously);
            CountPayload(keeper, strategy);
            Task pending = keeper.ScheduleGCInternal(throttle: false);
            await waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(runtime.Collections, Is.Empty);
            if (shutdown && i == cancellations - 1) keeper.Dispose();
            else keeper.CancelPendingGC();
            await pending.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(runtime.Collections, Is.Empty);
        }
        wait = false;
        await keeper.ScheduleGCInternal(throttle: false);
        if (shutdown)
        {
            Assert.That(runtime.Collections, Is.Empty);
        }
        else
        {
            await keeper.ScheduleGCInternal(throttle: false);
            Assert.That(runtime.Collections, Is.EqualTo(new[]
            {
                (GcLevel.Gen2, GCCollectionMode.Aggressive, GcCompaction.Full),
                (GcLevel.Gen1, GCCollectionMode.Forced, GcCompaction.No)
            }));
        }
    }

    private static void CountPayload(GCKeeper keeper, IGCStrategy strategy)
    {
        strategy.GetForcedGCParams().Returns((GcLevel.NoGC, GcCompaction.No));
        using (keeper.TryStartNoGCRegion()) { }
        strategy.GetForcedGCParams().Returns((GcLevel.Gen1, GcCompaction.No));
    }

    [Test]
    public void Disposed_payload_schedules_collection_without_requiring_entry([Values] bool dispatch, [Values] bool refuse)
    {
        List<IThreadPoolWorkItem> queued = [];
        RegionRuntime runtime = new() { Refuse = refuse };
        IGCStrategy strategy = Substitute.For<IGCStrategy>();
        strategy.CanStartNoGCRegion().Returns(true);
        strategy.GetForcedGCParams().Returns((GcLevel.NoGC, GcCompaction.No));
        using GCKeeper keeper = new(strategy, NullLogManager.Instance, runtime, queued.Add);
        using IDisposable lease = keeper.TryStartNoGCRegion();
        if (dispatch) queued[0].Execute();
        lease.Dispose();
        lease.Dispose();
        strategy.Received(1).GetForcedGCParams();
    }

    [Test]
    public async Task Strategy_disallowed_payloads_do_not_accumulate_decommit_debt()
    {
        IGCStrategy strategy = Substitute.For<IGCStrategy>();
        strategy.GetForcedGCParams().Returns((GcLevel.NoGC, GcCompaction.No));
        strategy.CollectionsPerDecommit.Returns(25);
        RegionRuntime runtime = new();
        using GCKeeper keeper = new(strategy, NullLogManager.Instance, runtime, static _ => { });
        for (int i = 0; i < 100; i++)
        {
            using (keeper.TryStartNoGCRegion()) { }
        }
        strategy.DidNotReceive().GetForcedGCParams();
        strategy.CanStartNoGCRegion().Returns(true);
        CountPayload(keeper, strategy);
        await keeper.ScheduleGCInternal(throttle: false);
        Assert.That(runtime.Collections, Is.EqualTo(new[] { (GcLevel.Gen1, GCCollectionMode.Forced, GcCompaction.No) }));
    }

    private const long Mb = 1_000_000;

    /// <param name="level">NoGC keeps post-block collections, which run on their own, out of tests that read the estimate.</param>
    private static IGCStrategy ModeStrategy(NoGcRegionMode mode, long guardBytes = 0, GcLevel level = GcLevel.NoGC, int collectionsPerDecommit = -1)
    {
        IGCStrategy strategy = Substitute.For<IGCStrategy>();
        strategy.CanStartNoGCRegion().Returns(true);
        strategy.NoGCRegionMode.Returns(mode);
        strategy.NoGCRegionGuardBytes.Returns(guardBytes);
        strategy.GetForcedGCParams().Returns((level, GcCompaction.No));
        strategy.CollectionsPerDecommit.Returns(collectionsPerDecommit);
        return strategy;
    }

    /// <summary>A runtime with a gen0 budget of <paramref name="budgetMb"/> whose last collection had nothing allocated before it.</summary>
    private static RegionRuntime BudgetRuntime(long budgetMb)
    {
        RegionRuntime runtime = new() { Gen0Budget = budgetMb * Mb };
        runtime.RunGC();
        return runtime;
    }

    // The entry is a stop-the-world on the payload's path that collects nothing; whether or not it is made, the
    // collection after the payload is what leaves gen0's budget fresh for the next one, so it runs in every mode.
    [Test]
    public async Task Post_block_collection_and_decommit_run_in_every_mode([Values] NoGcRegionMode mode, [Values] bool covered)
    {
        RegionRuntime runtime = BudgetRuntime(1_000);
        runtime.AllocatedBytes = covered ? 0 : 950 * Mb;
        List<IThreadPoolWorkItem> queued = [];
        IGCStrategy strategy = ModeStrategy(mode, guardBytes: 100 * Mb, level: GcLevel.Gen1, collectionsPerDecommit: 2);
        using GCKeeper keeper = new(strategy, NullLogManager.Instance, runtime, queued.Add, static (_, _) => Task.FromResult(true));
        long entries = Interlocked.Read(ref Metrics.NoGcRegionEntries);
        long skips = Interlocked.Read(ref Metrics.NoGcRegionSkips);

        bool enters = mode == NoGcRegionMode.Always || (mode == NoGcRegionMode.Guard && !covered);
        using (IDisposable lease = keeper.TryStartNoGCRegion())
        {
            if (enters) queued[0].Execute();
            using (Assert.EnterMultipleScope())
            {
                Assert.That(queued, Has.Count.EqualTo(enters ? 1 : 0));
                Assert.That(runtime.IsActive, Is.EqualTo(enters));
                Assert.That(runtime.Collections, Is.Empty, "nothing is collected while the payload is processed");
                Assert.That(Interlocked.Read(ref Metrics.NoGcRegionEntries) - entries, Is.EqualTo(enters ? 1 : 0));
                Assert.That(Interlocked.Read(ref Metrics.NoGcRegionSkips) - skips, Is.EqualTo(enters ? 0 : 1));
            }
        }

        Assert.That(() => runtime.Collections.Count, Is.EqualTo(1).After(5000, 10), "the post-block collection runs");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(runtime.Collections[0], Is.EqualTo((GcLevel.Gen1, GCCollectionMode.Forced, GcCompaction.No)));
            Assert.That(runtime.IsActive, Is.False);
            Assert.That(runtime.Ends, Is.EqualTo(enters ? 1 : 0));
        }

        // Payloads without a region still count towards the decommit, which comes on schedule.
        CountPayload(keeper, strategy);
        await keeper.ScheduleGCInternal(throttle: false);
        Assert.That(runtime.Collections[^1], Is.EqualTo((GcLevel.Gen2, GCCollectionMode.Aggressive, GcCompaction.Full)));
    }

    // Budget 1000 MB, guard fixed at 100 MB: the block is covered while at least 100 MB are left.
    [TestCase(0, false)]
    [TestCase(850, false)]
    [TestCase(900, false, TestName = "Exactly the guard left")]
    [TestCase(901, true)]
    [TestCase(1_200, true, TestName = "Budget overrun")]
    public void Guard_enters_only_when_the_budget_left_is_below_its_threshold(long allocatedMb, bool enters)
    {
        RegionRuntime runtime = BudgetRuntime(1_000);
        List<IThreadPoolWorkItem> queued = [];
        using GCKeeper keeper = new(ModeStrategy(NoGcRegionMode.Guard, guardBytes: 100 * Mb), NullLogManager.Instance, runtime, queued.Add);
        keeper.TryStartNoGCRegion().Dispose();
        Assert.That(queued, Is.Empty);

        runtime.AllocatedBytes = allocatedMb * Mb;
        using IDisposable lease = keeper.TryStartNoGCRegion();
        Assert.That(queued, Has.Count.EqualTo(enters ? 1 : 0));
    }

    [Test]
    public void Guard_enters_when_the_budget_is_unknown([Values] bool unknown, [Values] bool automatic)
    {
        RegionRuntime runtime = BudgetRuntime(unknown ? -1 : 1_000);
        List<IThreadPoolWorkItem> queued = [];
        using GCKeeper keeper = new(ModeStrategy(NoGcRegionMode.Guard, guardBytes: automatic ? 0 : 100 * Mb), NullLogManager.Instance, runtime, queued.Add);
        using IDisposable lease = keeper.TryStartNoGCRegion();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(queued, Has.Count.EqualTo(unknown ? 1 : 0), "an unknown budget never covers the block");
            Assert.That(Metrics.NoGcRegionGuardBudgetLeftBytes, Is.EqualTo(unknown ? 0 : 1_000 * Mb));
            Assert.That(Metrics.NoGcRegionGuardGen0BudgetBytes, Is.EqualTo(unknown ? 0 : 1_000 * Mb));
            Assert.That(Metrics.NoGcRegionGuardThresholdBytes, Is.EqualTo(automatic ? (unknown ? 128 : 750) * Mb : 100 * Mb));
        }
    }

    [Test]
    public void Gen0_budget_estimate_follows_collections_and_regions()
    {
        GCKeeper.Gen0BudgetTracker tracker = new();
        long region = GCKeeper.Gen0BudgetTracker.RegionSohBudget;
        using (Assert.EnterMultipleScope())
        {
            // Before any collection is seen, everything allocated since start counts.
            Assert.That(Estimate(tracker, 100 * Mb, 0, 1_000 * Mb), Is.EqualTo((900 * Mb, 1_000 * Mb)));
            Assert.That(Estimate(tracker, 300 * Mb, 0, 1_000 * Mb), Is.EqualTo((700 * Mb, 1_000 * Mb)));
            Assert.That(Estimate(tracker, 300 * Mb, 0, 0), Is.EqualTo((GCKeeper.Gen0BudgetTracker.Unknown, 0L)));
            // A collection seen late is assumed right after the previous sample: what came after that counts.
            Assert.That(Estimate(tracker, 500 * Mb, 1, 1_000 * Mb), Is.EqualTo((800 * Mb, 1_000 * Mb)));
            // The keeper's own collection: its start is known.
            tracker.OnCollected(600 * Mb, 610 * Mb, 2);
            Assert.That(Estimate(tracker, 700 * Mb, 2, 1_000 * Mb), Is.EqualTo((900 * Mb, 1_000 * Mb)));
            // From a region's entry, the region's budget less what came since is what is left, whatever the runtime's.
            tracker.OnRegionEntered(800 * Mb, 2);
            Assert.That(Estimate(tracker, 900 * Mb, 2, 4_000 * Mb), Is.EqualTo((region - 100 * Mb, region)));
            Assert.That(Estimate(tracker, 900 * Mb, 2, 300 * Mb), Is.EqualTo((region - 100 * Mb, region)));
            // Until the next collection is seen.
            Assert.That(Estimate(tracker, 950 * Mb, 3, 4_000 * Mb), Is.EqualTo((3_950 * Mb, 4_000 * Mb)));
        }
    }

    private static (long Left, long StartBudget) Estimate(GCKeeper.Gen0BudgetTracker tracker, long allocated, long gcIndex, long budget) =>
        (tracker.EstimateLeft(allocated, gcIndex, budget, out long startBudget), startBudget);

    // T = max(3/4 x B0, 2 x A), A at least 1/8 of the region's 512 MB small-object budget; a fixed guard replaces it.
    [TestCase(0, 1_000, 64, 750, TestName = "{m}(three quarters of the budget)")]
    [TestCase(0, 1_000, 400, 800, TestName = "{m}(twice the block allocation)")]
    [TestCase(0, 168, 64, 128, TestName = "{m}(floor, 8 heaps)")]
    [TestCase(0, 0, 64, 128, TestName = "{m}(floor, unknown budget)")]
    [TestCase(300, 1_000, 400, 300, TestName = "{m}(fixed)")]
    public void Guard_threshold(long fixedMb, long budgetMb, long blockMb, long expectedMb)
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(GCKeeper.BlockAllocationTracker.Floor, Is.EqualTo(64 * Mb));
            Assert.That(GCKeeper.GuardThreshold(fixedMb * Mb, budgetMb * Mb, blockMb * Mb), Is.EqualTo(expectedMb * Mb));
        }
    }

    [Test]
    public void Block_allocation_maximum_leaves_out_the_warm_up_and_drops_out_after_two_buckets()
    {
        GCKeeper.BlockAllocationTracker tracker = new();
        long floor = GCKeeper.BlockAllocationTracker.Floor;
        const int bucket = GCKeeper.BlockAllocationTracker.BucketPayloads;
        Assert.That((bucket, GCKeeper.BlockAllocationTracker.WarmUpPayloads), Is.EqualTo((300, 20)));

        Assert.That(tracker.Maximum, Is.EqualTo(floor), "nothing recorded");
        for (int i = 0; i < GCKeeper.BlockAllocationTracker.WarmUpPayloads; i++) tracker.Record(1_000 * Mb);
        Assert.That(tracker.Maximum, Is.EqualTo(floor), "the warm-up is left out");
        tracker.Record(10 * Mb);
        Assert.That(tracker.Maximum, Is.EqualTo(floor), "less than the floor");

        // The first bucket holds 300 MB, the second 70 MB.
        tracker.Record(300 * Mb);
        for (int i = 2; i < bucket; i++) tracker.Record(70 * Mb);
        Assert.That(tracker.Maximum, Is.EqualTo(300 * Mb), "the first bucket is full and still counts");
        for (int i = 1; i < bucket; i++) tracker.Record(70 * Mb);
        Assert.That(tracker.Maximum, Is.EqualTo(300 * Mb), "the second bucket is not full yet");
        tracker.Record(70 * Mb);
        Assert.That(tracker.Maximum, Is.EqualTo(70 * Mb), "the first bucket drops out");
        for (int i = 0; i < bucket; i++) tracker.Record(0);
        Assert.That(tracker.Maximum, Is.EqualTo(floor), "the second bucket drops out");
    }

    // A payload's window runs from the guard's decision to the end of its lease. Past the warm-up, what it allocates
    // raises the threshold once twice that is more than 3/4 of the budget.
    [Test]
    public void Guard_threshold_follows_what_payloads_allocate_until_their_lease_ends()
    {
        RegionRuntime runtime = BudgetRuntime(1_000);
        List<IThreadPoolWorkItem> queued = [];
        using GCKeeper keeper = new(ModeStrategy(NoGcRegionMode.Guard), NullLogManager.Instance, runtime, queued.Add);
        for (int i = 0; i < GCKeeper.BlockAllocationTracker.WarmUpPayloads; i++)
        {
            using IDisposable warmUp = keeper.TryStartNoGCRegion();
            runtime.AllocatedBytes += 450 * Mb;
        }

        // The collection is seen as right after the last warm-up payload's start, so its 450 MB count: 550 MB left.
        runtime.RunGC();
        using (keeper.TryStartNoGCRegion())
        {
            AssertGuardGauges(threshold: 750 * Mb, left: 550 * Mb, gen0Budget: 1_000 * Mb, blockAllocation: 64 * Mb, "the warm-up is left out");
            runtime.AllocatedBytes += 100 * Mb;
            // Whatever happens between the decision and the end of the lease is in the payload's window.
            keeper.TryStartNoGCRegion().Dispose();
            runtime.AllocatedBytes += 300 * Mb;
        }

        int entries = queued.Count;
        using (keeper.TryStartNoGCRegion())
        {
            AssertGuardGauges(threshold: 800 * Mb, left: 150 * Mb, gen0Budget: 1_000 * Mb, blockAllocation: 400 * Mb, "the payload's 400 MB");
        }
        Assert.That(queued, Has.Count.EqualTo(entries + 1));

        // A fresh budget covers the block; 780 MB left is more than 3/4 of it but less than twice 400 MB.
        runtime.RunGC();
        keeper.TryStartNoGCRegion().Dispose();
        Assert.That(queued, Has.Count.EqualTo(entries + 1), "1000 MB left");
        runtime.AllocatedBytes += 220 * Mb;
        using IDisposable lease = keeper.TryStartNoGCRegion();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(queued, Has.Count.EqualTo(entries + 2), "780 MB left");
            Assert.That(Metrics.NoGcRegionGuardThresholdBytes, Is.EqualTo(800 * Mb));
        }
    }

    [Test]
    public void Guard_fixed_threshold_overrides_the_rule()
    {
        RegionRuntime runtime = BudgetRuntime(1_000);
        List<IThreadPoolWorkItem> queued = [];
        using GCKeeper keeper = new(ModeStrategy(NoGcRegionMode.Guard, guardBytes: 300 * Mb), NullLogManager.Instance, runtime, queued.Add);
        runtime.AllocatedBytes = 650 * Mb;
        keeper.TryStartNoGCRegion().Dispose();
        AssertGuardGauges(threshold: 300 * Mb, left: 350 * Mb, gen0Budget: 1_000 * Mb, blockAllocation: 64 * Mb, "the rule would ask for 750 MB");
        Assert.That(queued, Is.Empty);

        runtime.AllocatedBytes = 750 * Mb;
        using IDisposable lease = keeper.TryStartNoGCRegion();
        Assert.That(queued, Has.Count.EqualTo(1), "250 MB left");
    }

    private static void AssertGuardGauges(long threshold, long left, long gen0Budget, long blockAllocation, string message)
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Metrics.NoGcRegionGuardThresholdBytes, Is.EqualTo(threshold), message);
            Assert.That(Metrics.NoGcRegionGuardBudgetLeftBytes, Is.EqualTo(left), message);
            Assert.That(Metrics.NoGcRegionGuardGen0BudgetBytes, Is.EqualTo(gen0Budget), message);
            Assert.That(Metrics.NoGcRegionGuardBlockAllocationBytes, Is.EqualTo(blockAllocation), message);
        }
    }

    [Test]
    public async Task Guard_counts_from_the_start_of_the_keepers_own_collection()
    {
        RegionRuntime runtime = BudgetRuntime(1_000);
        List<IThreadPoolWorkItem> queued = [];
        IGCStrategy strategy = ModeStrategy(NoGcRegionMode.Guard, guardBytes: 100 * Mb);
        using GCKeeper keeper = new(strategy, NullLogManager.Instance, runtime, queued.Add, static (_, _) => Task.FromResult(true));
        keeper.TryStartNoGCRegion().Dispose();

        // The post-block collection, with 950 MB allocated before it.
        runtime.AllocatedBytes = 950 * Mb;
        strategy.GetForcedGCParams().Returns((GcLevel.Gen1, GcCompaction.No));
        await keeper.ScheduleGCInternal(throttle: false);
        strategy.GetForcedGCParams().Returns((GcLevel.NoGC, GcCompaction.No));

        // 50 MB since the collection: 950 MB left.
        runtime.AllocatedBytes = 1_000 * Mb;
        keeper.TryStartNoGCRegion().Dispose();
        Assert.That(queued, Is.Empty);

        // 920 MB since: 80 MB left.
        runtime.AllocatedBytes = 1_870 * Mb;
        using IDisposable lease = keeper.TryStartNoGCRegion();
        Assert.That(queued, Has.Count.EqualTo(1));
    }

    [Test]
    public void Guard_follows_runtime_collections_and_its_own_regions()
    {
        RegionRuntime runtime = BudgetRuntime(4_000);
        List<IThreadPoolWorkItem> queued = [];
        using GCKeeper keeper = new(ModeStrategy(NoGcRegionMode.Guard, guardBytes: 100 * Mb), NullLogManager.Instance, runtime, queued.Add);
        keeper.TryStartNoGCRegion().Dispose();

        // A collection the runtime ran on its own, seen at the next payload.
        runtime.RunGC();
        runtime.AllocatedBytes = 10 * Mb;
        keeper.TryStartNoGCRegion().Dispose();
        Assert.That(queued, Is.Empty, "3990 MB left");

        // 40 MB left: the guard enters, and from then on the region's budget is what is left.
        runtime.AllocatedBytes = 3_960 * Mb;
        using (keeper.TryStartNoGCRegion())
        {
            Assert.That(queued, Has.Count.EqualTo(1));
            queued[0].Execute();
        }
        runtime.AllocatedBytes += GCKeeper.Gen0BudgetTracker.RegionSohBudget - 200 * Mb;
        keeper.TryStartNoGCRegion().Dispose();
        Assert.That(queued, Has.Count.EqualTo(1), "the region's 200 MB left");
        runtime.AllocatedBytes += 150 * Mb;
        using IDisposable lease = keeper.TryStartNoGCRegion();
        Assert.That(queued, Has.Count.EqualTo(2), "the region's 50 MB left");
    }

    [Test]
    public void Collections_during_processing_are_counted_without_own_entries([Values] NoGcRegionMode mode)
    {
        RegionRuntime runtime = BudgetRuntime(1_000);
        List<IThreadPoolWorkItem> queued = [];
        using GCKeeper keeper = new(ModeStrategy(mode, guardBytes: 100 * Mb), NullLogManager.Instance, runtime, queued.Add);
        long withCollection = Interlocked.Read(ref Metrics.NewPayloadsWithCollection);
        long misses = Interlocked.Read(ref Metrics.NoGcRegionGuardMisses);

        // A block with its region entered (always), or covered by the budget (guard), and nothing collected.
        IDisposable quiet = keeper.TryStartNoGCRegion();
        if (queued.Count > 0) queued[^1].Execute();
        quiet.Dispose();
        quiet.Dispose();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Interlocked.Read(ref Metrics.NewPayloadsWithCollection) - withCollection, Is.Zero, "a region entry is not a collection");
            Assert.That(Interlocked.Read(ref Metrics.NoGcRegionGuardMisses) - misses, Is.Zero);
        }

        // A block during which the runtime collects.
        IDisposable busy = keeper.TryStartNoGCRegion();
        if (queued.Count > 1) queued[^1].Execute();
        runtime.RunGC();
        busy.Dispose();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Interlocked.Read(ref Metrics.NewPayloadsWithCollection) - withCollection, Is.EqualTo(1));
            Assert.That(Interlocked.Read(ref Metrics.NoGcRegionGuardMisses) - misses, Is.EqualTo(mode == NoGcRegionMode.Guard ? 1 : 0),
                "only a payload the guard let run without a region is a miss");
        }
    }

    // A payload can start while the previous one is still inside its region: newPayload answers before its block
    // is committed and keeps the region for that commit. The region has to cover both and end when the last one
    // leaves, otherwise the newcomer runs with collections resumed under it.
    [Test]
    public void Overlapping_payload_is_covered_until_the_last_one_leaves()
    {
        List<IThreadPoolWorkItem> queued = [];
        RegionRuntime runtime = new();
        using GCKeeper keeper = CreateRegionKeeper(runtime, queued.Add);
        long entries = Interlocked.Read(ref Metrics.NoGcRegionEntries);
        long skips = Interlocked.Read(ref Metrics.NoGcRegionSkips);

        IDisposable first = keeper.TryStartNoGCRegion();
        queued[0].Execute();
        Assert.That(runtime.IsActive, Is.True);

        IDisposable second = keeper.TryStartNoGCRegion();
        first.Dispose();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(runtime.IsActive, Is.True, "the region still covers the payload inside it");
            Assert.That(runtime.Ends, Is.Zero);
            Assert.That(queued, Has.Count.EqualTo(1), "the overlapping payload shares the region rather than admitting another");
            Assert.That(Interlocked.Read(ref Metrics.NoGcRegionEntries) - entries, Is.EqualTo(1), "a shared region is not an entry of its own");
            Assert.That(Interlocked.Read(ref Metrics.NoGcRegionSkips) - skips, Is.Zero, "nor a payload without a region");
        }

        second.Dispose();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(runtime.IsActive, Is.False);
            Assert.That(runtime.Ends, Is.EqualTo(1));
        }
    }

    // The chain of leases ends with the payload that admitted the region. A budget entered once and sized for one
    // payload cannot be stretched over a queue of them, and a chain that kept renewing itself would hold the
    // keeper's slot so that no region could be admitted again.
    [Test]
    public void Lease_chain_ends_with_the_payload_that_admitted_the_region()
    {
        List<IThreadPoolWorkItem> queued = [];
        RegionRuntime runtime = new();
        using GCKeeper keeper = CreateRegionKeeper(runtime, queued.Add);

        IDisposable first = keeper.TryStartNoGCRegion();
        queued[0].Execute();
        IDisposable second = keeper.TryStartNoGCRegion();
        first.Dispose();

        // The payload that admitted the region has let go, so this one is not taken into it.
        long entries = Interlocked.Read(ref Metrics.NoGcRegionEntries);
        long skips = Interlocked.Read(ref Metrics.NoGcRegionSkips);
        IDisposable third = keeper.TryStartNoGCRegion();
        second.Dispose();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(runtime.IsActive, Is.False, "the region ends with the payloads it took in");
            Assert.That(runtime.Ends, Is.EqualTo(1));
            // It started inside the region the others still held, so it is neither an entry nor a payload without one.
            Assert.That(Interlocked.Read(ref Metrics.NoGcRegionEntries) - entries, Is.Zero);
            Assert.That(Interlocked.Read(ref Metrics.NoGcRegionSkips) - skips, Is.Zero);
        }

        third.Dispose();
        using IDisposable fourth = keeper.TryStartNoGCRegion();
        queued[1].Execute();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(queued, Has.Count.EqualTo(2), "a later payload admits a region of its own");
            Assert.That(runtime.IsActive, Is.True);
            Assert.That(runtime.Starts, Is.EqualTo(2));
        }
    }

    // Shutdown ends the region whatever is still inside it.
    [Test]
    public void Shutdown_ends_a_shared_region()
    {
        List<IThreadPoolWorkItem> queued = [];
        RegionRuntime runtime = new();
        GCKeeper keeper = CreateRegionKeeper(runtime, queued.Add);

        using IDisposable first = keeper.TryStartNoGCRegion();
        queued[0].Execute();
        using IDisposable second = keeper.TryStartNoGCRegion();

        keeper.Dispose();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(runtime.IsActive, Is.False);
            Assert.That(runtime.Ends, Is.EqualTo(1));
        }
    }

    private static async Task AssertStopWaitsUntilReleased(GCKeeper keeper, ManualResetEventSlim reached, ManualResetEventSlim proceed, Task work)
    {
        Task stop = Task.CompletedTask;
        try
        {
            Assert.That(reached.Wait(TimeSpan.FromSeconds(5)), Is.True);
            stop = keeper.StopAsync();
            Assert.That(stop.IsCompleted, Is.False);
        }
        finally
        {
            proceed.Set();
            await Task.WhenAll(work, stop).WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private static GCKeeper CreateRegionKeeper(RegionRuntime runtime, Action<IThreadPoolWorkItem> queue)
    {
        IGCStrategy strategy = Substitute.For<IGCStrategy>();
        strategy.CanStartNoGCRegion().Returns(true);
        strategy.GetForcedGCParams().Returns((GcLevel.NoGC, GcCompaction.No));
        return new GCKeeper(strategy, NullLogManager.Instance, runtime, queue);
    }

    private sealed class RegionRuntime : IGcRegionRuntime
    {
        public List<(GcLevel, GCCollectionMode, GcCompaction)> Collections { get; } = [];
        public bool CollectionSucceeds { get; set; } = true;
        public Action? BeforeCollect { get; init; }
        public bool Collect(GcLevel generation, GCCollectionMode mode, GcCompaction compacting)
        {
            BeforeCollect?.Invoke();
            Collections.Add((generation, mode, compacting));
            if (CollectionSucceeds) RunGC();
            return CollectionSucceeds;
        }
        public int CollectionCount { get; private set; }
        public long AllocatedBytes { get; set; }
        public long Gen0Budget { get; set; } = -1;
        public bool CanReadGen0Budget { get; init; } = true;
        public long LastGcIndex { get; private set; }
        /// <summary>A collection the runtime runs: moves the count and the index.</summary>
        public void RunGC()
        {
            CollectionCount++;
            LastGcIndex++;
        }
        public Exception? EndFailure { get; init; }
        public Action? BeforeStart { get; init; }
        public Action? BeforeEnd { get; init; }
        public bool Refuse { get; init; }
        public bool Throw { get; init; }
        public int Starts { get; private set; }
        public int Ends { get; private set; }
        public bool IsActive { get; private set; }
        public bool TryStart(long totalSize, long lohSize)
        {
            BeforeStart?.Invoke();
            Starts++;
            if (Throw) throw new InvalidOperationException("Another no-GC region is active.");
            // An entry moves the count and leaves the index (gc.cpp, update_collection_counts_for_no_gc).
            if (!Refuse) CollectionCount++;
            return IsActive = !Refuse;
        }
        public void End()
        {
            BeforeEnd?.Invoke();
            Ends++;
            IsActive = false;
            if (EndFailure is not null) throw EndFailure;
        }
    }

    [Test]
    public async Task Cancelling_a_collection_does_not_cancel_a_later_collection()
    {
        using GCKeeper keeper = CreateKeeper();
        Task first = keeper.ScheduleGCInternal(throttle: false);
        keeper.CancelPendingGC();
        keeper.CancelPendingGC();
        await first.WaitAsync(TimeSpan.FromSeconds(5));

        Task second = keeper.ScheduleGCInternal(throttle: false);
        Assert.That(second.IsCompleted, Is.False);
        keeper.Dispose();
        await second.WaitAsync(TimeSpan.FromSeconds(5));
        keeper.CancelPendingGC();
        Assert.That(keeper.ScheduleGCInternal(throttle: false).IsCompletedSuccessfully, Is.True);
    }

    [Test]
    public async Task Cancelled_scheduled_collection_does_not_throttle_next_collection()
    {
        IGCStrategy strategy = Substitute.For<IGCStrategy>();
        strategy.PostBlockDelayMs.Returns(1);
        strategy.GetForcedGCParams().Returns((GcLevel.Gen1, GcCompaction.No));
        strategy.CollectionsPerDecommit.Returns(-1);
        RegionRuntime runtime = new();
        TaskCompletionSource firstDelay = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int delayCalls = 0;

        async Task<bool> Delay(int _, CancellationToken token)
        {
            if (Interlocked.Increment(ref delayCalls) == 1)
            {
                firstDelay.SetResult();
                try
                {
                    await Task.Delay(Timeout.Infinite, token);
                }
                catch (OperationCanceledException)
                {
                    return false;
                }
            }

            return true;
        }

        using GCKeeper keeper = new(strategy, NullLogManager.Instance, runtime, static _ => { }, Delay);
        Task first = keeper.ScheduleGCInternal(throttle: true);
        await firstDelay.Task.WaitAsync(TimeSpan.FromSeconds(5));
        keeper.CancelPendingGC();
        await first.WaitAsync(TimeSpan.FromSeconds(5));

        await keeper.ScheduleGCInternal(throttle: true).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(runtime.Collections, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task Successful_collection_throttles_the_next_collection()
    {
        IGCStrategy strategy = Substitute.For<IGCStrategy>();
        strategy.GetForcedGCParams().Returns((GcLevel.Gen1, GcCompaction.No));
        strategy.CollectionsPerDecommit.Returns(-1);
        RegionRuntime runtime = new();
        using GCKeeper keeper = new(strategy, NullLogManager.Instance, runtime, static _ => { });

        await keeper.ScheduleGCInternal(throttle: true);
        await keeper.ScheduleGCInternal(throttle: true);

        Assert.That(runtime.Collections, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task Cancellation_after_yield_prevents_collection([Values(0, -1)] int delay)
    {
        using GCKeeper keeper = CreateKeeper(delay);
        await CancelCollectionAfterYield(keeper);
    }

    private static async Task CancelCollectionAfterYield(GCKeeper keeper)
    {
        PausedContext paused = new();
        SynchronizationContext? previous = SynchronizationContext.Current;
        Task pending;
        try
        {
            SynchronizationContext.SetSynchronizationContext(paused);
            pending = keeper.ScheduleGCInternal(throttle: false);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }

        keeper.CancelPendingGC();
        paused.Resume();
        await pending.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Test]
    public async Task Cancellation_does_not_wait_for_committed_collection([Values] bool dispose, [Values(0, 1)] int delay)
    {
        using ManualResetEventSlim executing = new(false);
        using ManualResetEventSlim release = new(false);
        IGCStrategy strategy = Substitute.For<IGCStrategy>();
        strategy.CanStartNoGCRegion().Returns(true);
        strategy.PostBlockDelayMs.Returns(delay);
        strategy.GetForcedGCParams().Returns((GcLevel.Gen1, GcCompaction.No));
        strategy.CollectionsPerDecommit.Returns(-1);
        TaskCompletionSource collectionReleased = new(TaskCreationOptions.RunContinuationsAsynchronously);
        RegionRuntime runtime = new() { BeforeCollect = () => { executing.Set(); release.Wait(); collectionReleased.SetResult(); } };
        using GCKeeper keeper = new(strategy, NullLogManager.Instance, runtime, static _ => { },
            static (_, _) => Task.FromResult(true));
        Task collection = Task.Run(() => keeper.TryStartNoGCRegion().Dispose());
        Task cancellation = Task.CompletedTask;
        try
        {
            Assert.That(executing.Wait(TimeSpan.FromSeconds(5)), Is.True);
            cancellation = Task.Run(() =>
            {
                if (dispose) keeper.Dispose();
                else keeper.CancelPendingGC();
            });
            await cancellation.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            release.Set();
            await Task.WhenAll(collection, cancellation, collectionReleased.Task).WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Test]
    public async Task Stop_waits_for_committed_collection()
    {
        using ManualResetEventSlim collecting = new(false);
        using ManualResetEventSlim proceed = new(false);
        IGCStrategy strategy = Substitute.For<IGCStrategy>();
        strategy.CanStartNoGCRegion().Returns(true);
        strategy.GetForcedGCParams().Returns((GcLevel.Gen1, GcCompaction.No));
        strategy.CollectionsPerDecommit.Returns(-1);
        RegionRuntime runtime = new() { BeforeCollect = () => { collecting.Set(); proceed.Wait(); } };
        using GCKeeper keeper = new(strategy, NullLogManager.Instance, runtime, static item => item.Execute());
        keeper.TryStartNoGCRegion().Dispose();
        await AssertStopWaitsUntilReleased(keeper, collecting, proceed, Task.CompletedTask);
        Assert.That(runtime.Collections, Has.Count.EqualTo(1));
    }

    private static GCKeeper CreateKeeper(int delay = 60_000)
    {
        IGCStrategy strategy = Substitute.For<IGCStrategy>();
        strategy.PostBlockDelayMs.Returns(delay);
        strategy.GetForcedGCParams().Returns((GcLevel.Gen1, GcCompaction.Yes));
        strategy.CollectionsPerDecommit.Returns(_ => throw new AssertionException("Cancelled collection reached GC execution."));
        return new GCKeeper(strategy, NullLogManager.Instance);
    }

    private sealed class PausedContext : SynchronizationContext
    {
        private Action? _continuation;
        public override void Post(SendOrPostCallback callback, object? state) => _continuation = () => callback(state);
        public void Resume() => _continuation!();
    }
}
