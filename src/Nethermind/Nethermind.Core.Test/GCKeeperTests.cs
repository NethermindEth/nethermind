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
            return CollectionSucceeds;
        }
        public Exception? EndFailure { get; init; }
        public Action? BeforeStart { get; init; }
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
            return IsActive = !Refuse;
        }
        public void End()
        {
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
