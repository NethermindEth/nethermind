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
    public async Task Decommit_interval_preserves_sentinels([Values(-1, 0, 50)] int interval)
    {
        IGCStrategy strategy = Substitute.For<IGCStrategy>();
        strategy.CanStartNoGCRegion().Returns(true);
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

    // A payload can start while the previous one is still inside its region: newPayload answers before its block
    // is committed and keeps the region for that commit. The region has to cover both and end when the last one
    // leaves, otherwise the newcomer runs with collections resumed under it.
    [Test]
    public void Overlapping_payload_is_covered_until_the_last_one_leaves()
    {
        List<IThreadPoolWorkItem> queued = [];
        RegionRuntime runtime = new();
        using GCKeeper keeper = CreateRegionKeeper(runtime, queued.Add);

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
        IDisposable third = keeper.TryStartNoGCRegion();
        second.Dispose();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(runtime.IsActive, Is.False, "the region ends with the payloads it took in");
            Assert.That(runtime.Ends, Is.EqualTo(1));
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
            if (CollectionSucceeds) RunGC((int)generation);
            return CollectionSucceeds;
        }
        public long Gen0Budget { get; set; } = -1;
        public long LastGcIndex { get; private set; }
        private readonly int[] _counts = new int[3];
        public int CollectionCount(int generation) => _counts[generation];
        /// <summary>A collection the runtime runs: counts up to its generation and moves the index.</summary>
        public void RunGC(int generation)
        {
            for (int i = 0; i <= Math.Min(generation, 2); i++) _counts[i]++;
            LastGcIndex++;
        }
        /// <summary>Region entries move every count and leave the index (gc.cpp update_collection_counts_for_no_gc).</summary>
        private void CountEntry()
        {
            for (int i = 0; i < 3; i++) _counts[i]++;
        }
        public Exception? EndFailure { get; init; }
        public Action? BeforeStart { get; init; }
        public Action? BeforeEnd { get; init; }
        public bool Refuse { get; set; }
        public bool Throw { get; init; }
        public int Starts { get; private set; }
        public int Ends { get; private set; }
        public bool IsActive { get; private set; }
        /// <summary>The runtime leaves the region by itself, as when its budget is spent.</summary>
        public void EndByRuntime() => IsActive = false;
        public long AllocatedBytes { get; set; }
        public bool TryStart(long totalSize, long lohSize)
        {
            BeforeStart?.Invoke();
            Starts++;
            if (Throw) throw new InvalidOperationException("Another no-GC region is active.");
            if (!Refuse) CountEntry();
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

    [Test]
    public void Payload_takes_over_a_pre_entered_region_and_owns_it()
    {
        PreEntryRig rig = new();
        using GCKeeper keeper = rig.Keeper;
        long takenOver = Interlocked.Read(ref Metrics.NoGcRegionPreEntriesTakenOver);
        Assert.That(keeper.PrepareNoGCRegion(), Is.True);
        rig.Queued[0].Execute();
        rig.Runtime.AllocatedBytes += 10 * 1024 * 1024;

        IDisposable first = keeper.TryStartNoGCRegion();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(rig.Queued, Has.Count.EqualTo(1), "the payload enters nothing of its own");
            Assert.That(rig.Runtime.Starts, Is.EqualTo(1));
            Assert.That(rig.Runtime.IsActive, Is.True);
            Assert.That(Interlocked.Read(ref Metrics.NoGcRegionPreEntriesTakenOver), Is.GreaterThan(takenOver));
        }

        // As the region's owner the payload admits an overlapping one, and its release ends the chain.
        IDisposable second = keeper.TryStartNoGCRegion();
        first.Dispose();
        IDisposable third = keeper.TryStartNoGCRegion();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(rig.Runtime.IsActive, Is.True, "the overlapping payload is still inside");
            Assert.That(rig.Queued, Has.Count.EqualTo(1));
        }

        second.Dispose();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(rig.Runtime.IsActive, Is.False, "the region ends with the payloads it took in, not the third");
            Assert.That(rig.Runtime.Ends, Is.EqualTo(1));
        }

        third.Dispose();
        rig.Strategy.Received(3).GetForcedGCParams();

        // The expiry of a pre-entry that was taken over does nothing.
        rig.CompleteDelays(GCKeeper.PreEntryTimeoutMs);
        using IDisposable next = keeper.TryStartNoGCRegion();
        Assert.That(rig.Queued, Has.Count.EqualTo(2), "a later payload admits a region of its own");
    }

    [Test]
    public void Unused_pre_entry_expires_without_scheduling_a_collection()
    {
        PreEntryRig rig = new();
        using GCKeeper keeper = rig.Keeper;
        long expired = Interlocked.Read(ref Metrics.NoGcRegionPreEntriesExpired);
        keeper.PrepareNoGCRegion();
        rig.Queued[0].Execute();
        Assert.That(rig.Runtime.IsActive, Is.True);

        rig.CompleteDelays(GCKeeper.PreEntryTimeoutMs);
        Assert.That(() => rig.Runtime.Ends, Is.EqualTo(1).After(5000, 10));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(rig.Runtime.IsActive, Is.False);
            Assert.That(() => Interlocked.Read(ref Metrics.NoGcRegionPreEntriesExpired), Is.GreaterThan(expired).After(5000, 10));
            rig.Strategy.DidNotReceive().GetForcedGCParams();
        }

        using IDisposable payload = keeper.TryStartNoGCRegion();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(rig.Queued, Has.Count.EqualTo(2), "the slot is free for the payload's own region");
            Assert.That(GCScheduler.MarkGCPaused(), Is.False, "the payload holds the scheduler pause again");
        }
    }

    [Test]
    public void Pre_entry_that_allocated_too_much_is_ended_and_the_payload_enters_its_own([Values] bool overBudget)
    {
        PreEntryRig rig = new();
        using GCKeeper keeper = rig.Keeper;
        long stale = Interlocked.Read(ref Metrics.NoGcRegionPreEntriesStale);
        keeper.PrepareNoGCRegion();
        rig.Queued[0].Execute();
        rig.Runtime.AllocatedBytes += GCKeeper.PreEntryMaxAllocatedBytes + (overBudget ? 0 : -1);

        using IDisposable payload = keeper.TryStartNoGCRegion();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(rig.Runtime.Ends, Is.EqualTo(overBudget ? 1 : 0));
            Assert.That(rig.Queued, Has.Count.EqualTo(overBudget ? 2 : 1));
            Assert.That(Interlocked.Read(ref Metrics.NoGcRegionPreEntriesStale) - stale, Is.EqualTo(overBudget ? 1 : 0));
        }

        if (overBudget) rig.Queued[1].Execute();
        Assert.That(rig.Runtime.IsActive, Is.True);
        payload.Dispose();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(rig.Runtime.IsActive, Is.False);
            Assert.That(rig.Runtime.Ends, Is.EqualTo(overBudget ? 2 : 1));
            Assert.That(rig.Runtime.Starts, Is.EqualTo(overBudget ? 2 : 1));
        }
    }

    [Test]
    public void Pre_entry_past_its_timeout_is_not_taken_over_before_its_expiry_fires([Values] bool entered)
    {
        PreEntryRig rig = new();
        using GCKeeper keeper = rig.Keeper;
        keeper.PrepareNoGCRegion();
        if (entered) rig.Queued[0].Execute();
        rig.AdvanceMs(GCKeeper.PreEntryTimeoutMs);

        using IDisposable payload = keeper.TryStartNoGCRegion();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(rig.Runtime.Ends, Is.EqualTo(entered ? 1 : 0));
            Assert.That(rig.Queued, Has.Count.EqualTo(2), "the payload enters a region of its own");
        }

        // A stale entry still queued does not enter once it runs.
        if (!entered) rig.Queued[0].Execute();
        rig.Queued[1].Execute();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(rig.Runtime.Starts, Is.EqualTo(entered ? 2 : 1));
            Assert.That(rig.Runtime.IsActive, Is.True);
        }
    }

    [Test]
    public void Stale_pre_entry_hands_the_scheduler_pause_to_the_payload()
    {
        // The pre-entry found the scheduler paused by someone else, who let go before the payload arrived.
        Assert.That(GCScheduler.MarkGCPaused(), Is.True);
        PreEntryRig rig = new();
        using GCKeeper keeper = rig.Keeper;
        keeper.PrepareNoGCRegion();
        rig.Queued[0].Execute();
        GCScheduler.MarkGCResumed();
        rig.AdvanceMs(GCKeeper.PreEntryTimeoutMs);

        IDisposable payload = keeper.TryStartNoGCRegion();
        Assert.That(GCScheduler.MarkGCPaused(), Is.False, "the payload holds the pause");
        payload.Dispose();
        Assert.That(GCScheduler.MarkGCPaused(), Is.True, "the payload let go of the pause");
        GCScheduler.MarkGCResumed();
    }

    [Test]
    public async Task Payload_takes_over_a_pre_entry_that_is_still_queued_or_running([Values] bool running)
    {
        using ManualResetEventSlim entering = new(false);
        using ManualResetEventSlim proceed = new(false);
        PreEntryRig rig = new(running ? () => { entering.Set(); proceed.Wait(); } : null);
        using GCKeeper keeper = rig.Keeper;
        keeper.PrepareNoGCRegion();
        Task entry = Task.CompletedTask;
        if (running)
        {
            entry = Task.Run(rig.Queued[0].Execute);
            Assert.That(entering.Wait(TimeSpan.FromSeconds(5)), Is.True);
        }

        IDisposable payload = keeper.TryStartNoGCRegion();
        Assert.That(rig.Queued, Has.Count.EqualTo(1), "the payload waits on the pending entry rather than queueing another");
        if (running) proceed.Set();
        else rig.Queued[0].Execute();
        await entry.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(rig.Runtime.IsActive, Is.True);

        // Taken over: its expiry no longer ends the region under the payload.
        rig.CompleteDelays(GCKeeper.PreEntryTimeoutMs);
        await Task.Delay(50);
        Assert.That(rig.Runtime.IsActive, Is.True);

        payload.Dispose();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(rig.Runtime.IsActive, Is.False);
            Assert.That(rig.Runtime.Starts, Is.EqualTo(1));
            Assert.That(rig.Runtime.Ends, Is.EqualTo(1));
        }
    }

    [Test]
    public void Failed_pre_entry_frees_the_slot_at_once()
    {
        PreEntryRig rig = new();
        using GCKeeper keeper = rig.Keeper;
        long failed = Interlocked.Read(ref Metrics.NoGcRegionPreEntriesFailed);
        rig.Runtime.Refuse = true;
        keeper.PrepareNoGCRegion();
        rig.Queued[0].Execute();
        rig.Runtime.Refuse = false;
        Assert.That(Interlocked.Read(ref Metrics.NoGcRegionPreEntriesFailed) - failed, Is.EqualTo(1));

        // Nor the scheduler pause it took: that is let go of with the failure, not at the expiry.
        Assert.That(GCScheduler.MarkGCPaused(), Is.True, "the failed pre-entry still pauses the GC scheduler");
        GCScheduler.MarkGCResumed();

        Assert.That(keeper.PrepareNoGCRegion(), Is.True, "the failed pre-entry does not hold the slot until its expiry");
        rig.Queued[1].Execute();
        using IDisposable payload = keeper.TryStartNoGCRegion();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(rig.Queued, Has.Count.EqualTo(2));
            Assert.That(rig.Runtime.IsActive, Is.True);
        }
    }

    [Test]
    public async Task Shutdown_ends_a_pre_entered_region([Values] bool entered)
    {
        PreEntryRig rig = new();
        GCKeeper keeper = rig.Keeper;
        keeper.PrepareNoGCRegion();
        if (entered) rig.Queued[0].Execute();

        Task stop = keeper.StopAsync();
        if (!entered)
        {
            Assert.That(stop.IsCompleted, Is.False, "stop waits for the queued pre-entry");
            rig.Queued[0].Execute();
        }

        await stop.WaitAsync(TimeSpan.FromSeconds(5));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(rig.Runtime.IsActive, Is.False);
            Assert.That(rig.Runtime.Starts, Is.EqualTo(entered ? 1 : 0));
            Assert.That(rig.Runtime.Ends, Is.EqualTo(entered ? 1 : 0));
            Assert.That(keeper.PrepareNoGCRegion(), Is.False);
        }

        rig.CompleteDelays(GCKeeper.PreEntryTimeoutMs);
        await Task.Delay(50);
        Assert.That(rig.Runtime.Ends, Is.EqualTo(entered ? 1 : 0));
    }

    [Test]
    public void Pre_entry_is_skipped_when_not_allowed_or_a_region_is_held([Values] bool disallowed)
    {
        PreEntryRig rig = new();
        using GCKeeper keeper = rig.Keeper;
        IDisposable? payload = null;
        if (disallowed) rig.Strategy.CanStartNoGCRegion().Returns(false);
        else payload = keeper.TryStartNoGCRegion();

        Assert.That(keeper.PrepareNoGCRegion(), Is.False);
        Assert.That(rig.Queued, Has.Count.EqualTo(disallowed ? 0 : 1));
        payload?.Dispose();
    }

    [Test]
    public void Pre_entry_is_not_scheduled_while_the_region_is_off()
    {
        PreEntryRig rig = new();
        rig.Strategy.CanStartNoGCRegion().Returns(false);
        using GCKeeper keeper = rig.Keeper;
        keeper.SchedulePrepareNoGCRegion();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(rig.Queued, Is.Empty);
            Assert.That(rig.Delays, Is.Empty);
        }
    }

    [Test]
    public void Scheduled_pre_entry_waits_its_delay_and_is_not_doubled()
    {
        PreEntryRig rig = new();
        using GCKeeper keeper = rig.Keeper;
        keeper.SchedulePrepareNoGCRegion();
        keeper.SchedulePrepareNoGCRegion();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(rig.Delays, Is.EqualTo(new[] { GCKeeper.PreEntryDelayMs }));
            Assert.That(rig.Queued, Is.Empty);
        }

        rig.CompleteDelays(GCKeeper.PreEntryDelayMs);
        Assert.That(() => rig.Queued.Count, Is.EqualTo(1).After(5000, 10));

        // Once entered, another getBlobs costs no second region while the first is held.
        Assert.That(() => { keeper.SchedulePrepareNoGCRegion(); return rig.PendingDelays(GCKeeper.PreEntryDelayMs); }, Is.EqualTo(1).After(5000, 10));
        rig.CompleteDelays(GCKeeper.PreEntryDelayMs);
        Assert.That(() => rig.PendingDelays(GCKeeper.PreEntryDelayMs), Is.Zero.After(5000, 10));
        Thread.Sleep(50);
        Assert.That(rig.Queued, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task Payload_arriving_while_an_expired_pre_entry_is_ending_enters_its_own_once_it_has_ended()
    {
        using ManualResetEventSlim ending = new(false);
        using ManualResetEventSlim proceed = new(false);
        PreEntryRig rig = new(beforeEnd: () => { ending.Set(); proceed.Wait(); });
        using GCKeeper keeper = rig.Keeper;
        keeper.PrepareNoGCRegion();
        rig.Queued[0].Execute();

        // The expiry has let go of the region and is ending it, but it has not left the keeper's slot yet.
        rig.CompleteDelays(GCKeeper.PreEntryTimeoutMs);
        Assert.That(ending.Wait(TimeSpan.FromSeconds(5)), Is.True);

        IDisposable payload;
        try
        {
            payload = keeper.TryStartNoGCRegion();
            Assert.That(rig.Queued, Has.Count.EqualTo(1), "the payload's entry waits for the region on its way out");
        }
        finally
        {
            proceed.Set();
        }

        Assert.That(() => rig.Queued.Count, Is.EqualTo(2).After(5000, 10), "the payload enters its own region, not none");
        rig.Queued[1].Execute();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(rig.Runtime.Starts, Is.EqualTo(2));
            Assert.That(rig.Runtime.IsActive, Is.True);
        }

        payload.Dispose();
        await Task.Delay(50);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(rig.Runtime.IsActive, Is.False);
            Assert.That(rig.Runtime.Ends, Is.EqualTo(2));
        }
    }

    [Test]
    public async Task Payload_finding_a_stale_pre_entry_still_being_entered_enters_its_own_once_it_has_ended()
    {
        using ManualResetEventSlim entering = new(false);
        using ManualResetEventSlim proceed = new(false);
        PreEntryRig rig = new(() => { entering.Set(); proceed.Wait(); });
        using GCKeeper keeper = rig.Keeper;
        keeper.PrepareNoGCRegion();
        Task entry = Task.Run(rig.Queued[0].Execute);
        Assert.That(entering.Wait(TimeSpan.FromSeconds(5)), Is.True);
        rig.AdvanceMs(GCKeeper.PreEntryTimeoutMs);

        IDisposable payload;
        try
        {
            payload = keeper.TryStartNoGCRegion();
            Assert.That(rig.Queued, Has.Count.EqualTo(1));
        }
        finally
        {
            proceed.Set();
        }

        using IDisposable lease = payload;
        await entry.WaitAsync(TimeSpan.FromSeconds(5));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(rig.Runtime.Ends, Is.EqualTo(1), "the stale region is ended as soon as its entry completes");
            Assert.That(rig.Queued, Has.Count.EqualTo(2), "then the payload's own entry is queued");
        }

        rig.Queued[1].Execute();
        Assert.That(rig.Runtime.IsActive, Is.True);
    }

    [Test]
    public void GetBlobs_answered_while_a_pre_entry_is_being_made_keeps_its_schedule()
    {
        PreEntryRig rig = new();
        using GCKeeper keeper = rig.Keeper;
        // A getBlobs answered while the previous pre-entry is being queued.
        rig.OnQueue = keeper.SchedulePrepareNoGCRegion;
        keeper.SchedulePrepareNoGCRegion();
        rig.CompleteDelays(GCKeeper.PreEntryDelayMs);
        Assert.That(() => rig.PendingDelays(GCKeeper.PreEntryDelayMs), Is.EqualTo(1).After(5000, 10));
        rig.OnQueue = null;
        Thread.Sleep(50);

        // That schedule is still the one pending: another getBlobs does not start a second.
        keeper.SchedulePrepareNoGCRegion();
        Assert.That(rig.PendingDelays(GCKeeper.PreEntryDelayMs), Is.EqualTo(1));
    }

    // BENCH gc-region-pre-slot: the region entered ahead of the slot.

    [TestCase(500, 0, 12_000, TestName = "Early in the head's slot")]
    [TestCase(10_999, 0, 12_000, TestName = "Just before the next slot's lead")]
    [TestCase(11_000, 0, 24_000, TestName = "Next slot's lead begun")]
    [TestCase(40_000, 0, 48_000, TestName = "Missed slots")]
    [TestCase(-30_000, 0, 12_000, TestName = "Head ahead of the clock")]
    [TestCase(12_000, 1, 24_000, TestName = "No lead, at the slot start")]
    [TestCase(12_500, 2, 12_000, TestName = "Negative lead, inside the current slot before its fire time")]
    [TestCase(12_999, 2, 12_000, TestName = "Negative lead, just before the fire time")]
    [TestCase(13_000, 2, 24_000, TestName = "Negative lead, at the fire time")]
    [TestCase(13_001, 2, 24_000, TestName = "Negative lead, just after the fire time")]
    [TestCase(36_500, 2, 36_000, TestName = "Negative lead, missed slots, before the fire time")]
    [TestCase(40_000, 2, 48_000, TestName = "Negative lead, missed slots, after the fire time")]
    public void Next_slot_start_follows_the_head(long nowSinceHeadMs, int leadKind, long expectedSinceHeadMs)
    {
        const long head = 1_700_000_000_000;
        long lead = leadKind switch { 1 => 0, 2 => -1_000, _ => 1_000 };
        Assert.That(GCKeeper.NextSlotStartMs(head, 12_000, head + nowSinceHeadMs, lead) - head, Is.EqualTo(expectedSinceHeadMs));
    }

    [Test]
    public void Slot_trigger_enters_the_region_its_lead_before_the_slot_and_re_arms()
    {
        PreEntryRig rig = new();
        using GCKeeper keeper = rig.Keeper;
        const ulong head = 1_700_000_000;
        rig.UnixMs = (long)head * 1000 + 2_000;
        long slotEntries = Interlocked.Read(ref Metrics.NoGcRegionPreSlotEntries);

        keeper.OnNewHead(head);
        keeper.OnNewHead(head);
        Assert.That(() => rig.PendingDelays(9_000), Is.EqualTo(1).After(5000, 10), "one loop, firing 1 s before the next slot");
        Assert.That(rig.Queued, Is.Empty);

        rig.UnixMs += 9_000;
        rig.CompleteDelays(9_000);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(() => rig.Queued.Count, Is.EqualTo(1).After(5000, 10));
            Assert.That(() => rig.PendingDelays(12_000), Is.EqualTo(1).After(5000, 10), "re-armed for the following slot");
            Assert.That(rig.PendingDelays(GCKeeper.PreEntrySettings.Default.SlotExpiryMs), Is.EqualTo(1), "expiry measured from entry");
            Assert.That(Interlocked.Read(ref Metrics.NoGcRegionPreSlotEntries) - slotEntries, Is.EqualTo(1));
        }
    }

    [Test]
    public void Negative_lead_fires_after_the_slot_start_and_an_early_block_skips_it([Values] bool earlyBlock)
    {
        PreEntryRig rig = new(settings: GCKeeper.PreEntrySettings.Default with { SlotLeadMs = -1_000 });
        using GCKeeper keeper = rig.Keeper;
        const ulong head = 1_700_000_000;
        // Half a second into the slot after the head, its block not in yet.
        rig.UnixMs = (long)head * 1000 + 12_500;
        keeper.OnNewHead(head);
        Assert.That(() => rig.PendingDelays(500), Is.EqualTo(1).After(5000, 10), "fires 1 s after this slot's start");

        if (earlyBlock) keeper.OnNewHead(head + 12);
        rig.UnixMs += 500;
        rig.CompleteDelays(500);
        Assert.That(() => rig.PendingDelays(12_000), Is.EqualTo(1).After(5000, 10), "re-armed for 1 s after the next slot's start");
        Thread.Sleep(50);
        Assert.That(rig.Queued, Has.Count.EqualTo(earlyBlock ? 0 : 1), "an early block leaves nothing to enter ahead of");
    }

    [TestCase("off", false, false)]
    [TestCase("getblobs", true, false)]
    [TestCase("slot", false, true)]
    [TestCase("both", true, true)]
    [TestCase(null, true, true)]
    public void Mode_selects_the_triggers(string? mode, bool getBlobs, bool slot)
    {
        PreEntryRig rig = new(settings: GCKeeper.PreEntrySettings.Parse(mode, null, null, null));
        using GCKeeper keeper = rig.Keeper;
        rig.UnixMs = 1_700_000_000_000 + 2_000;
        keeper.SchedulePrepareNoGCRegion();
        keeper.OnNewHead(1_700_000_000);
        Thread.Sleep(50);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(rig.PendingDelays(GCKeeper.PreEntryDelayMs), Is.EqualTo(getBlobs ? 1 : 0));
            Assert.That(rig.PendingDelays(9_000), Is.EqualTo(slot ? 1 : 0));
        }
    }

    [Test]
    public void Bench_settings_parse_with_defaults()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(GCKeeper.PreEntrySettings.Parse(null, null, null, null), Is.EqualTo(GCKeeper.PreEntrySettings.Default));
            Assert.That(GCKeeper.PreEntrySettings.Default, Is.EqualTo(new GCKeeper.PreEntrySettings(GCKeeper.PreEntryMode.Both, 1_000, 6_000, 128_000_000)));
            Assert.That(GCKeeper.PreEntrySettings.Parse(" Slot ", "500", "4000", "64"),
                Is.EqualTo(new GCKeeper.PreEntrySettings(GCKeeper.PreEntryMode.Slot, 500, 4_000, 64_000_000)));
            Assert.That(GCKeeper.PreEntrySettings.Parse("x", "y", "0", "y"), Is.EqualTo(GCKeeper.PreEntrySettings.Default));
            Assert.That(GCKeeper.PreEntrySettings.Parse(null, "-1000", null, null).SlotLeadMs, Is.EqualTo(-1_000), "after the slot start");
        }
    }

    [Test]
    public void Payload_takes_over_a_slot_entered_region_within_its_window([Values] bool pastWindow)
    {
        PreEntryRig rig = new();
        using GCKeeper keeper = rig.Keeper;
        long takenOver = Interlocked.Read(ref Metrics.NoGcRegionPreSlotEntriesTakenOver);
        long stale = Interlocked.Read(ref Metrics.NoGcRegionPreSlotEntriesStale);
        Assert.That(keeper.PrepareNoGCRegion(GCKeeper.PreEntryTrigger.Slot), Is.True);
        rig.Queued[0].Execute();
        // Longer than a getBlobs pre-entry lives, within the slot's window unless past it.
        rig.AdvanceMs(pastWindow ? GCKeeper.PreEntrySettings.Default.SlotExpiryMs : GCKeeper.PreEntrySettings.Default.SlotExpiryMs - 1_000);

        using IDisposable payload = keeper.TryStartNoGCRegion();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(rig.Queued, Has.Count.EqualTo(pastWindow ? 2 : 1));
            Assert.That(rig.Runtime.Ends, Is.EqualTo(pastWindow ? 1 : 0));
            Assert.That(Interlocked.Read(ref Metrics.NoGcRegionPreSlotEntriesTakenOver) - takenOver, Is.EqualTo(pastWindow ? 0 : 1));
            Assert.That(Interlocked.Read(ref Metrics.NoGcRegionPreSlotEntriesStale) - stale, Is.EqualTo(pastWindow ? 1 : 0));
        }
    }

    [Test]
    public void Unused_slot_entered_region_expires_after_its_configured_window()
    {
        PreEntryRig rig = new(settings: GCKeeper.PreEntrySettings.Default with { SlotExpiryMs = 4_500 });
        using GCKeeper keeper = rig.Keeper;
        long expired = Interlocked.Read(ref Metrics.NoGcRegionPreSlotEntriesExpired);
        keeper.PrepareNoGCRegion(GCKeeper.PreEntryTrigger.Slot);
        rig.Queued[0].Execute();
        Assert.That(rig.PendingDelays(4_500), Is.EqualTo(1));

        rig.CompleteDelays(GCKeeper.PreEntryTimeoutMs);
        Thread.Sleep(50);
        Assert.That(rig.Runtime.IsActive, Is.True, "not at a getBlobs pre-entry's expiry");

        rig.CompleteDelays(4_500);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(() => rig.Runtime.Ends, Is.EqualTo(1).After(5000, 10));
            Assert.That(() => Interlocked.Read(ref Metrics.NoGcRegionPreSlotEntriesExpired) - expired, Is.EqualTo(1).After(5000, 10));
        }
    }

    [TestCase(0, true, 1, TestName = "Payload's own region ended by the runtime")]
    [TestCase(1, true, 1, TestName = "Taken-over region ended by the runtime")]
    [TestCase(0, false, 0, TestName = "Payload's own region ended by the payload")]
    [TestCase(2, true, 0, TestName = "Unused pre-entry ended by the runtime")]
    public void Region_ended_by_the_runtime_under_its_payload_is_counted(int kind, bool runtimeEnds, int expected)
    {
        PreEntryRig rig = new();
        using GCKeeper keeper = rig.Keeper;
        long ended = Interlocked.Read(ref Metrics.NoGcRegionEndedByRuntime);
        long expired = Interlocked.Read(ref Metrics.NoGcRegionPreSlotEntriesExpired);
        IDisposable? payload = null;
        if (kind == 0)
        {
            payload = keeper.TryStartNoGCRegion();
            rig.Queued[0].Execute();
        }
        else
        {
            keeper.PrepareNoGCRegion(GCKeeper.PreEntryTrigger.Slot);
            rig.Queued[0].Execute();
            if (kind == 1) payload = keeper.TryStartNoGCRegion();
        }

        if (runtimeEnds) rig.Runtime.EndByRuntime();
        if (payload is not null) payload.Dispose();
        else rig.CompleteDelays(GCKeeper.PreEntrySettings.Default.SlotExpiryMs);

        if (payload is null)
        {
            Assert.That(() => Interlocked.Read(ref Metrics.NoGcRegionPreSlotEntriesExpired) - expired, Is.EqualTo(1).After(5000, 10));
        }

        Assert.That(Interlocked.Read(ref Metrics.NoGcRegionEndedByRuntime) - ended, Is.EqualTo(expected));
    }

    [Test]
    public void Allocation_at_hand_over_is_summed_per_trigger_with_its_max([Values] bool slot, [Values] bool overCap)
    {
        PreEntryRig rig = new();
        using GCKeeper keeper = rig.Keeper;
        long delta = (overCap ? GCKeeper.PreEntrySettings.Default.MaxAllocatedBytes : 1_000) + Interlocked.Increment(ref _allocationCase);
        long takeover = Interlocked.Read(ref Metrics.NoGcRegionPreEntryAllocatedBytesAtTakeover);
        long slotTakeover = Interlocked.Read(ref Metrics.NoGcRegionPreSlotAllocatedBytesAtTakeover);
        long stale = Interlocked.Read(ref Metrics.NoGcRegionPreEntryAllocatedBytesAtStale);
        long slotStale = Interlocked.Read(ref Metrics.NoGcRegionPreSlotAllocatedBytesAtStale);
        long max = Interlocked.Read(ref Metrics.NoGcRegionPreEntryAllocatedBytesAtTakeoverMax);
        keeper.PrepareNoGCRegion(slot ? GCKeeper.PreEntryTrigger.Slot : GCKeeper.PreEntryTrigger.GetBlobs);
        rig.Runtime.AllocatedBytes = 5_000_000;
        rig.Queued[0].Execute();
        rig.Runtime.AllocatedBytes += delta;

        using IDisposable payload = keeper.TryStartNoGCRegion();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Interlocked.Read(ref Metrics.NoGcRegionPreEntryAllocatedBytesAtTakeover) - takeover, Is.EqualTo(overCap ? 0 : delta));
            Assert.That(Interlocked.Read(ref Metrics.NoGcRegionPreSlotAllocatedBytesAtTakeover) - slotTakeover, Is.EqualTo(overCap || !slot ? 0 : delta));
            Assert.That(Interlocked.Read(ref Metrics.NoGcRegionPreEntryAllocatedBytesAtStale) - stale, Is.EqualTo(overCap ? delta : 0));
            Assert.That(Interlocked.Read(ref Metrics.NoGcRegionPreSlotAllocatedBytesAtStale) - slotStale, Is.EqualTo(overCap && slot ? delta : 0));
            Assert.That(Interlocked.Read(ref Metrics.NoGcRegionPreEntryAllocatedBytesAtTakeoverMax), Is.EqualTo(overCap ? max : Math.Max(max, delta)));
            if (slot && !overCap)
                Assert.That(Interlocked.Read(ref Metrics.NoGcRegionPreSlotAllocatedBytesAtTakeoverMax), Is.GreaterThanOrEqualTo(delta));
        }
    }

    private static long _allocationCase;

    [Test]
    public void Dispose_stops_the_slot_loop()
    {
        PreEntryRig rig = new();
        GCKeeper keeper = rig.Keeper;
        rig.UnixMs = 1_700_000_000_000 + 2_000;
        keeper.OnNewHead(1_700_000_000);
        Assert.That(() => rig.PendingDelays(9_000), Is.EqualTo(1).After(5000, 10));

        keeper.Dispose();
        Assert.That(() => rig.PendingDelays(9_000), Is.Zero.After(5000, 10));
        keeper.OnNewHead(1_700_000_012);
        Thread.Sleep(50);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(rig.Delays, Has.Count.EqualTo(1), "no loop after dispose");
            Assert.That(rig.Queued, Is.Empty);
        }
    }

    // BENCH gc-region-guard: whether the payload enters a region of its own, and the re-commit after the decommit.

    private const long Mb = 1_000_000;

    private static GCKeeper.PreEntrySettings EntrySettings(string entry, long guardBytes = GCKeeper.DefaultGuardBytes, bool recommit = false) =>
        GCKeeper.PreEntrySettings.Parse(null, null, null, null, entry) with { GuardBytes = guardBytes, RecommitAfterDecommit = recommit };

    /// <param name="level">NoGC keeps post-block collections, which run on their own, out of tests that read the estimate.</param>
    private static IGCStrategy PostBlockStrategy(int collectionsPerDecommit = -1, GcLevel level = GcLevel.Gen1, int postBlockDelayMs = 0)
    {
        IGCStrategy strategy = Substitute.For<IGCStrategy>();
        strategy.CanStartNoGCRegion().Returns(true);
        strategy.PostBlockDelayMs.Returns(postBlockDelayMs);
        strategy.GetForcedGCParams().Returns((level, GcCompaction.No));
        strategy.CollectionsPerDecommit.Returns(collectionsPerDecommit);
        return strategy;
    }

    /// <summary>A runtime with a gen0 budget of <paramref name="budgetMb"/> whose last collection had nothing allocated before it.</summary>
    private static RegionRuntime BudgetRuntime(long budgetMb)
    {
        RegionRuntime runtime = new() { Gen0Budget = budgetMb * Mb };
        runtime.RunGC(0);
        return runtime;
    }

    [Test]
    public async Task Never_and_guard_skip_the_entry_and_keep_the_post_block_collection([Values("never", "guard")] string entry)
    {
        RegionRuntime runtime = BudgetRuntime(1_000);
        List<IThreadPoolWorkItem> queued = [];
        IGCStrategy strategy = PostBlockStrategy(collectionsPerDecommit: 2);
        using GCKeeper keeper = new(strategy, NullLogManager.Instance, runtime, queued.Add, static (_, _) => Task.FromResult(true),
            settings: EntrySettings(entry));
        long skips = Interlocked.Read(ref Metrics.NoGcRegionPayloadSkips);
        long entries = Interlocked.Read(ref Metrics.NoGcRegionPayloadEntries);

        keeper.TryStartNoGCRegion().Dispose();

        Assert.That(() => runtime.Collections.Count, Is.EqualTo(1).After(5000, 10), "the post-block collection runs");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(queued, Is.Empty, "no entry is queued");
            Assert.That(runtime.Starts, Is.Zero);
            Assert.That(runtime.Collections[0], Is.EqualTo((GcLevel.Gen1, GCCollectionMode.Forced, GcCompaction.No)));
            Assert.That(Interlocked.Read(ref Metrics.NoGcRegionPayloadSkips) - skips, Is.EqualTo(1));
            Assert.That(Interlocked.Read(ref Metrics.NoGcRegionPayloadEntries) - entries, Is.Zero);
        }

        // Skipped payloads still count towards the decommit, which comes on schedule.
        CountPayload(keeper, strategy);
        await keeper.ScheduleGCInternal(throttle: false);
        Assert.That(runtime.Collections[^1], Is.EqualTo((GcLevel.Gen2, GCCollectionMode.Aggressive, GcCompaction.Full)));
    }

    [Test]
    public void Always_still_enters_and_counts_the_entry()
    {
        RegionRuntime runtime = BudgetRuntime(1_000);
        List<IThreadPoolWorkItem> queued = [];
        using GCKeeper keeper = new(PostBlockStrategy(level: GcLevel.NoGC), NullLogManager.Instance, runtime, queued.Add, settings: EntrySettings("always"));
        long entries = Interlocked.Read(ref Metrics.NoGcRegionPayloadEntries);
        long skips = Interlocked.Read(ref Metrics.NoGcRegionPayloadSkips);
        using IDisposable lease = keeper.TryStartNoGCRegion();
        queued[0].Execute();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(runtime.IsActive, Is.True);
            Assert.That(Interlocked.Read(ref Metrics.NoGcRegionPayloadEntries) - entries, Is.EqualTo(1));
            Assert.That(Interlocked.Read(ref Metrics.NoGcRegionPayloadSkips) - skips, Is.Zero);
        }
    }

    // Budget 1000 MB, guard 100 MB: the block is covered while at least 100 MB are left.
    [TestCase(0, false)]
    [TestCase(850, false)]
    [TestCase(900, false, TestName = "Exactly the guard left")]
    [TestCase(901, true)]
    [TestCase(1_200, true, TestName = "Budget overrun")]
    public void Guard_enters_only_when_the_budget_left_is_below_its_threshold(long allocatedMb, bool enters)
    {
        RegionRuntime runtime = BudgetRuntime(1_000);
        List<IThreadPoolWorkItem> queued = [];
        using GCKeeper keeper = new(PostBlockStrategy(level: GcLevel.NoGC), NullLogManager.Instance, runtime, queued.Add,
            settings: EntrySettings("guard", guardBytes: 100 * Mb));
        keeper.TryStartNoGCRegion().Dispose();
        Assert.That(queued, Is.Empty);

        runtime.AllocatedBytes = allocatedMb * Mb;
        using IDisposable lease = keeper.TryStartNoGCRegion();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(queued, Has.Count.EqualTo(enters ? 1 : 0));
            Assert.That(Interlocked.Read(ref Metrics.NoGcRegionGuardBudgetLeftBytes), Is.EqualTo((1_000 - allocatedMb) * Mb));
            Assert.That(Interlocked.Read(ref Metrics.NoGcRegionGuardGen0BudgetBytes), Is.EqualTo(1_000 * Mb));
        }
    }

    [Test]
    public void Guard_enters_when_the_budget_is_unknown_and_follows_an_override()
    {
        RegionRuntime runtime = BudgetRuntime(-1);
        List<IThreadPoolWorkItem> queued = [];
        using (GCKeeper keeper = new(PostBlockStrategy(level: GcLevel.NoGC), NullLogManager.Instance, runtime, queued.Add, settings: EntrySettings("guard")))
        {
            keeper.TryStartNoGCRegion().Dispose();
            Assert.That(queued, Has.Count.EqualTo(1), "an unknown budget never covers the block");
        }

        queued.Clear();
        using GCKeeper pinned = new(PostBlockStrategy(level: GcLevel.NoGC), NullLogManager.Instance, runtime, queued.Add,
            settings: EntrySettings("guard") with { GuardBudgetBytes = 2_000 * Mb });
        pinned.TryStartNoGCRegion().Dispose();
        Assert.That(queued, Is.Empty, "BENCH_GC_REGION_GUARD_BUDGET_MB stands in for the runtime's budget");
    }

    [Test]
    public void Gen0_budget_estimate_follows_collections_and_regions()
    {
        GCKeeper.Gen0BudgetTracker tracker = new();
        long region = GCKeeper.Gen0BudgetTracker.RegionSohBudget;
        using (Assert.EnterMultipleScope())
        {
            // Before any collection is seen, everything allocated since start counts.
            Assert.That(tracker.EstimateLeft(100 * Mb, 0, 1_000 * Mb), Is.EqualTo(900 * Mb));
            Assert.That(tracker.EstimateLeft(300 * Mb, 0, 1_000 * Mb), Is.EqualTo(700 * Mb));
            Assert.That(tracker.EstimateLeft(300 * Mb, 0, 0), Is.EqualTo(GCKeeper.Gen0BudgetTracker.Unknown));
            // A collection seen late is assumed right after the previous sample: what came after that counts.
            Assert.That(tracker.EstimateLeft(500 * Mb, 1, 1_000 * Mb), Is.EqualTo(800 * Mb));
            // The keeper's own collection: its start is known.
            tracker.OnCollected(600 * Mb, 610 * Mb, 2);
            Assert.That(tracker.EstimateLeft(700 * Mb, 2, 1_000 * Mb), Is.EqualTo(900 * Mb));
            // From a region's entry, the region's budget less what came since is what is left, whatever the runtime's.
            tracker.OnRegionEntered(800 * Mb, 2);
            Assert.That(tracker.EstimateLeft(900 * Mb, 2, 4_000 * Mb), Is.EqualTo(region - 100 * Mb));
            Assert.That(tracker.EstimateLeft(900 * Mb, 2, 300 * Mb), Is.EqualTo(region - 100 * Mb));
            // Until the next collection is seen.
            Assert.That(tracker.EstimateLeft(950 * Mb, 3, 4_000 * Mb), Is.EqualTo(3_950 * Mb));
        }
    }

    [Test]
    public void Guard_follows_pre_entries_collections_and_its_own_regions()
    {
        RegionRuntime runtime = BudgetRuntime(4_000);
        List<IThreadPoolWorkItem> queued = [];
        using GCKeeper keeper = new(PostBlockStrategy(level: GcLevel.NoGC), NullLogManager.Instance, runtime, queued.Add,
            settings: EntrySettings("guard", guardBytes: 100 * Mb));
        keeper.TryStartNoGCRegion().Dispose();
        // 50 MB left: a pre-entry is made, and the payload takes it over rather than entering its own.
        runtime.AllocatedBytes = 3_950 * Mb;
        Assert.That(keeper.PrepareNoGCRegion(), Is.True);
        queued[0].Execute();
        keeper.TryStartNoGCRegion().Dispose();
        Assert.That(queued, Has.Count.EqualTo(1));

        // A collection the runtime ran on its own, seen at the next payload.
        runtime.RunGC(1);
        runtime.AllocatedBytes = 3_960 * Mb;
        keeper.TryStartNoGCRegion().Dispose();
        Assert.That(queued, Has.Count.EqualTo(1), "3990 MB left");

        // 40 MB left: the guard enters, and from then on the region's budget is what is left.
        runtime.AllocatedBytes = 7_910 * Mb;
        using (keeper.TryStartNoGCRegion())
        {
            Assert.That(queued, Has.Count.EqualTo(2));
            queued[1].Execute();
        }
        runtime.AllocatedBytes += GCKeeper.Gen0BudgetTracker.RegionSohBudget - 200 * Mb;
        keeper.TryStartNoGCRegion().Dispose();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(queued, Has.Count.EqualTo(2), "the region's 200 MB left");
            Assert.That(Interlocked.Read(ref Metrics.NoGcRegionGuardBudgetLeftBytes), Is.EqualTo(200 * Mb));
        }
    }

    [Test]
    public async Task Guard_counts_from_the_start_of_the_keepers_own_collection()
    {
        RegionRuntime runtime = BudgetRuntime(1_000);
        List<IThreadPoolWorkItem> queued = [];
        IGCStrategy strategy = PostBlockStrategy(level: GcLevel.NoGC);
        using GCKeeper keeper = new(strategy, NullLogManager.Instance, runtime, queued.Add, static (_, _) => Task.FromResult(true),
            settings: EntrySettings("guard", guardBytes: 100 * Mb));
        keeper.TryStartNoGCRegion().Dispose();

        // The post-block collection, with 950 MB allocated before it.
        runtime.AllocatedBytes = 950 * Mb;
        strategy.GetForcedGCParams().Returns((GcLevel.Gen1, GcCompaction.No));
        await keeper.ScheduleGCInternal(throttle: false);
        strategy.GetForcedGCParams().Returns((GcLevel.NoGC, GcCompaction.No));

        runtime.AllocatedBytes = 1_000 * Mb;
        keeper.TryStartNoGCRegion().Dispose();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(queued, Is.Empty, "950 MB left since the collection");
            Assert.That(Interlocked.Read(ref Metrics.NoGcRegionGuardBudgetLeftBytes), Is.EqualTo(950 * Mb));
        }
    }

    [Test]
    public void Pre_entry_follows_the_payload_entry_mode([Values("always", "guard", "never")] string entry, [Values] bool covered)
    {
        RegionRuntime runtime = BudgetRuntime(1_000);
        runtime.AllocatedBytes = covered ? 0 : 950 * Mb;
        List<IThreadPoolWorkItem> queued = [];
        List<int> delays = [];
        using GCKeeper keeper = new(PostBlockStrategy(level: GcLevel.NoGC), NullLogManager.Instance, runtime, queued.Add,
            (ms, _) =>
            {
                lock (delays) delays.Add(ms);
                return new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously).Task;
            },
            settings: EntrySettings(entry, guardBytes: 100 * Mb), unixTimeMs: static () => 1_700_000_002_000);
        long skipped = Interlocked.Read(ref Metrics.NoGcRegionPreEntriesSkippedByGuard);

        bool prepared = keeper.PrepareNoGCRegion();
        keeper.SchedulePrepareNoGCRegion();
        keeper.OnNewHead(1_700_000_000);
        Thread.Sleep(50);

        bool expected = entry == "always" || (entry == "guard" && !covered);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(prepared, Is.EqualTo(expected));
            Assert.That(queued, Has.Count.EqualTo(expected ? 1 : 0));
            Assert.That(Interlocked.Read(ref Metrics.NoGcRegionPreEntriesSkippedByGuard) - skipped, Is.EqualTo(entry == "guard" && covered ? 1 : 0));
            // getBlobs' delay and the slot loop's wait, or neither with never.
            lock (delays) Assert.That(delays, entry == "never" ? Is.Empty : Has.Count.GreaterThanOrEqualTo(2));
        }
    }

    [Test]
    public void Collections_during_processing_are_counted_without_own_entries([Values("always", "guard", "never")] string entry)
    {
        RegionRuntime runtime = BudgetRuntime(1_000);
        List<IThreadPoolWorkItem> queued = [];
        using GCKeeper keeper = new(PostBlockStrategy(level: GcLevel.NoGC), NullLogManager.Instance, runtime, queued.Add, settings: EntrySettings(entry));
        long measured = Interlocked.Read(ref Metrics.NoGcRegionPayloadsMeasured);
        long withGc = Interlocked.Read(ref Metrics.NoGcRegionPayloadsWithCollectionInProcessing);
        long gen0 = Interlocked.Read(ref Metrics.NoGcRegionGen0CollectionsInProcessing);
        long gen1 = Interlocked.Read(ref Metrics.NoGcRegionGen1CollectionsInProcessing);
        long gen2 = Interlocked.Read(ref Metrics.NoGcRegionGen2CollectionsInProcessing);
        long allocated = Interlocked.Read(ref Metrics.NoGcRegionPayloadAllocatedBytes);
        long covered = Interlocked.Read(ref Metrics.NoGcRegionGuardCovered);
        long misses = Interlocked.Read(ref Metrics.NoGcRegionGuardMisses);

        // A block with its region entered (always) and nothing collected.
        IDisposable quiet = keeper.TryStartNoGCRegion();
        if (queued.Count > 0) queued[^1].Execute();
        runtime.AllocatedBytes += 5 * Mb;
        quiet.Dispose();
        quiet.Dispose();
        // A block with a gen1 collection in it (which ends its region, if any).
        IDisposable busy = keeper.TryStartNoGCRegion();
        if (queued.Count > 1) queued[^1].Execute();
        runtime.EndByRuntime();
        runtime.RunGC(1);
        busy.Dispose();

        bool estimated = entry != "always";
        using (Assert.EnterMultipleScope())
        {
            Assert.That(queued, Has.Count.EqualTo(estimated ? 0 : 2));
            Assert.That(Interlocked.Read(ref Metrics.NoGcRegionPayloadsMeasured) - measured, Is.EqualTo(2));
            Assert.That(Interlocked.Read(ref Metrics.NoGcRegionPayloadsWithCollectionInProcessing) - withGc, Is.EqualTo(1));
            Assert.That(Interlocked.Read(ref Metrics.NoGcRegionGen0CollectionsInProcessing) - gen0, Is.EqualTo(1));
            Assert.That(Interlocked.Read(ref Metrics.NoGcRegionGen1CollectionsInProcessing) - gen1, Is.EqualTo(1));
            Assert.That(Interlocked.Read(ref Metrics.NoGcRegionGen2CollectionsInProcessing) - gen2, Is.Zero);
            Assert.That(Interlocked.Read(ref Metrics.NoGcRegionPayloadAllocatedBytes) - allocated, Is.EqualTo(5 * Mb));
            Assert.That(Interlocked.Read(ref Metrics.NoGcRegionPayloadAllocatedBytesMax), Is.GreaterThanOrEqualTo(5 * Mb));
            // Guard and never judge both blocks covered (~1000 MB left), and the second one had a collection.
            Assert.That(Interlocked.Read(ref Metrics.NoGcRegionGuardCovered) - covered, Is.EqualTo(estimated ? 2 : 0));
            Assert.That(Interlocked.Read(ref Metrics.NoGcRegionGuardMisses) - misses, Is.EqualTo(estimated ? 1 : 0));
        }
    }

    [Test]
    public async Task Recommit_runs_right_after_the_decommit_and_only_then([Values] bool enabled, [Values] bool decommit)
    {
        RegionRuntime? runtime = null;
        int collectionsAtStart = -1;
        runtime = new RegionRuntime { BeforeStart = () => collectionsAtStart = runtime!.Collections.Count };
        using GCKeeper keeper = new(PostBlockStrategy(decommit ? 0 : -1), NullLogManager.Instance, runtime, static _ => { },
            static (_, _) => Task.FromResult(true), settings: EntrySettings("always", recommit: enabled));
        long recommits = Interlocked.Read(ref Metrics.NoGcRegionRecommits);

        await keeper.ScheduleGCInternal(throttle: false);

        int expected = enabled && decommit ? 1 : 0;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(runtime.Collections[0].Item2, Is.EqualTo(decommit ? GCCollectionMode.Aggressive : GCCollectionMode.Forced));
            Assert.That(runtime.Starts, Is.EqualTo(expected));
            if (expected == 1) Assert.That(collectionsAtStart, Is.EqualTo(1), "entered after the collection");
            Assert.That(runtime.Ends, Is.EqualTo(expected), "the throwaway region is ended at once");
            Assert.That(runtime.IsActive, Is.False);
            Assert.That(Interlocked.Read(ref Metrics.NoGcRegionRecommits) - recommits, Is.EqualTo(expected));
        }
    }

    [Test]
    public async Task Recommit_is_skipped_when_a_payload_comes_during_the_decommit([Values] bool cancelOnly)
    {
        List<IThreadPoolWorkItem> queued = [];
        IDisposable? payload = null;
        GCKeeper? keeper = null;
        RegionRuntime runtime = new()
        {
            BeforeCollect = () =>
            {
                // As JsonRpcService does for engine_newPayload; then (unless cancelOnly) the payload takes the slot.
                keeper!.CancelPendingGC();
                if (!cancelOnly) payload = keeper.TryStartNoGCRegion();
            }
        };
        using (keeper = new(PostBlockStrategy(0), NullLogManager.Instance, runtime, queued.Add, static (_, _) => Task.FromResult(true),
            settings: EntrySettings("always", recommit: true)))
        {
            long skipped = Interlocked.Read(ref Metrics.NoGcRegionRecommitsSkipped);
            await keeper.ScheduleGCInternal(throttle: false);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(runtime.Collections, Has.Count.EqualTo(1));
                Assert.That(runtime.Starts, Is.Zero, "no throwaway region");
                Assert.That(Interlocked.Read(ref Metrics.NoGcRegionRecommitsSkipped) - skipped, Is.EqualTo(1));
            }

            if (!cancelOnly)
            {
                queued[0].Execute();
                Assert.That(runtime.IsActive, Is.True, "the payload's own region");
            }
            payload?.Dispose();
        }
    }

    [Test]
    public async Task Recommit_is_skipped_while_a_region_holds_the_slot()
    {
        RegionRuntime runtime = new();
        List<IThreadPoolWorkItem> queued = [];
        // The pre-entry's expiry (3 s) never fires; the post-block wait (1 s) and the idle wait (2 s) do.
        using GCKeeper keeper = new(PostBlockStrategy(0, postBlockDelayMs: 1_000), NullLogManager.Instance, runtime, queued.Add,
            static (ms, _) => ms == GCKeeper.PreEntryTimeoutMs ? new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously).Task : Task.FromResult(true),
            settings: EntrySettings("always", recommit: true));
        Assert.That(keeper.PrepareNoGCRegion(), Is.True, "a pre-entry holds the slot, its entry still queued");
        long skipped = Interlocked.Read(ref Metrics.NoGcRegionRecommitsSkipped);
        await keeper.ScheduleGCInternal(throttle: false);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(runtime.Collections, Has.Count.EqualTo(1));
            Assert.That(runtime.Starts, Is.Zero);
            Assert.That(Interlocked.Read(ref Metrics.NoGcRegionRecommitsSkipped) - skipped, Is.EqualTo(1));
        }
    }

    [Test]
    public async Task Payload_entry_waits_for_the_recommits_region_to_end()
    {
        List<IThreadPoolWorkItem> queued = [];
        GCKeeper? keeper = null;
        IDisposable? payload = null;
        Task entry = Task.CompletedTask;
        bool entryDoneInside = true;
        RegionRuntime runtime = new()
        {
            BeforeEnd = () =>
            {
                if (payload is not null) return;
                // The throwaway region is active: a payload arrives and its entry is dispatched.
                payload = keeper!.TryStartNoGCRegion();
                entry = Task.Run(queued[0].Execute);
                entryDoneInside = entry.Wait(200);
            }
        };
        using (keeper = new(PostBlockStrategy(0), NullLogManager.Instance, runtime, queued.Add, static (_, _) => Task.FromResult(true),
            settings: EntrySettings("always", recommit: true)))
        {
            await keeper.ScheduleGCInternal(throttle: false);
            await entry.WaitAsync(TimeSpan.FromSeconds(5));
            using (Assert.EnterMultipleScope())
            {
                Assert.That(entryDoneInside, Is.False, "the payload's entry waits for the throwaway region to end");
                Assert.That(runtime.Starts, Is.EqualTo(2));
                Assert.That(runtime.IsActive, Is.True, "the payload's region is its own, not ended by the re-commit");
            }
            payload!.Dispose();
            Assert.That(runtime.IsActive, Is.False);
        }
    }

    [Test]
    public void Bench_entry_settings_parse_with_defaults()
    {
        using (Assert.EnterMultipleScope())
        {
            GCKeeper.PreEntrySettings defaults = GCKeeper.PreEntrySettings.Parse(null, null, null, null, null, null, null, null);
            Assert.That(defaults, Is.EqualTo(GCKeeper.PreEntrySettings.Default));
            Assert.That(defaults.Entry, Is.EqualTo(GCKeeper.RegionEntry.Always));
            Assert.That(defaults.GuardBytes, Is.EqualTo(256 * Mb));
            Assert.That(defaults.GuardBudgetBytes, Is.Zero);
            Assert.That(defaults.RecommitAfterDecommit, Is.False);
            Assert.That(GCKeeper.PreEntrySettings.Parse("getblobs", null, null, null, " Guard ", "64", "2048", "1"),
                Is.EqualTo(new GCKeeper.PreEntrySettings(GCKeeper.PreEntryMode.GetBlobs, 1_000, 6_000, 128 * Mb, GCKeeper.RegionEntry.Guard, 64 * Mb, 2_048 * Mb, true)));
            Assert.That(GCKeeper.PreEntrySettings.Parse(null, null, null, null, "NEVER", "x", "0", "true"),
                Is.EqualTo(GCKeeper.PreEntrySettings.Default with { Entry = GCKeeper.RegionEntry.Never, RecommitAfterDecommit = true }));
            Assert.That(GCKeeper.PreEntrySettings.Parse(null, null, null, null, "x", "-1", "-5", "yes"), Is.EqualTo(GCKeeper.PreEntrySettings.Default));
            Assert.That(GCKeeper.PreEntrySettings.Parse(null, null, null, null, null, "0", null, null).GuardBytes, Is.Zero);
        }
    }

    private sealed class PreEntryRig
    {
        private long _now = 1_000_000;
        private readonly List<(int Ms, TaskCompletionSource<bool> Done)> _pending = [];

        private long _unixMs;

        public PreEntryRig(Action? beforeStart = null, Action? beforeEnd = null, GCKeeper.PreEntrySettings? settings = null)
        {
            Runtime = new RegionRuntime { BeforeStart = beforeStart, BeforeEnd = beforeEnd };
            Strategy.CanStartNoGCRegion().Returns(true);
            Strategy.GetForcedGCParams().Returns((GcLevel.NoGC, GcCompaction.No));
            Strategy.SecondsPerSlot.Returns(12UL);
            Keeper = new GCKeeper(Strategy, NullLogManager.Instance, Runtime, Queue, Delay, () => Interlocked.Read(ref _now),
                settings, () => Interlocked.Read(ref _unixMs));
        }

        public long UnixMs
        {
            get => Interlocked.Read(ref _unixMs);
            set => Interlocked.Exchange(ref _unixMs, value);
        }

        public Action? OnQueue { get; set; }

        public RegionRuntime Runtime { get; }
        public IGCStrategy Strategy { get; } = Substitute.For<IGCStrategy>();
        public List<IThreadPoolWorkItem> Queued { get; } = [];
        public List<int> Delays { get; } = [];
        public GCKeeper Keeper { get; }

        public void AdvanceMs(int ms) => Interlocked.Add(ref _now, ms * System.Diagnostics.Stopwatch.Frequency / 1000);

        public void CompleteDelays(int ms)
        {
            lock (_pending)
            {
                foreach ((int Ms, TaskCompletionSource<bool> Done) delay in _pending)
                {
                    if (delay.Ms == ms) delay.Done.TrySetResult(true);
                }
            }
        }

        public int PendingDelays(int ms)
        {
            lock (_pending)
            {
                int count = 0;
                foreach ((int Ms, TaskCompletionSource<bool> Done) delay in _pending)
                {
                    if (delay.Ms == ms && !delay.Done.Task.IsCompleted) count++;
                }
                return count;
            }
        }

        private void Queue(IThreadPoolWorkItem item)
        {
            lock (Queued) Queued.Add(item);
            OnQueue?.Invoke();
        }

        private Task<bool> Delay(int ms, CancellationToken token)
        {
            TaskCompletionSource<bool> done = new(TaskCreationOptions.RunContinuationsAsynchronously);
            token.Register(() => done.TrySetResult(false));
            lock (_pending)
            {
                Delays.Add(ms);
                _pending.Add((ms, done));
            }
            return done.Task;
        }
    }
}
