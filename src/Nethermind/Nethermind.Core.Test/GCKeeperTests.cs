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

    [Test]
    public async Task Decommit_recommits_the_region_budget_off_the_payload_path([Values] bool decommit)
    {
        IGCStrategy strategy = Substitute.For<IGCStrategy>();
        strategy.CanStartNoGCRegion().Returns(true);
        strategy.GetForcedGCParams().Returns((GcLevel.Gen1, GcCompaction.No));
        strategy.CollectionsPerDecommit.Returns(decommit ? 0 : -1);
        RegionRuntime runtime = new();
        using GCKeeper keeper = new(strategy, NullLogManager.Instance, runtime, static _ => { }, static (_, _) => Task.FromResult(true));
        long entries = Interlocked.Read(ref Metrics.NoGcRegionEntries);
        long recommits = Interlocked.Read(ref Metrics.NoGcRegionRecommits);

        await keeper.ScheduleGCInternal(throttle: false);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(runtime.Collections, Has.Count.EqualTo(1));
            Assert.That(runtime.Operations, Is.EqualTo(decommit ? new[] { "collect", "start", "end" } : new[] { "collect" }));
            Assert.That(runtime.IsActive, Is.False);
            Assert.That(Interlocked.Read(ref Metrics.NoGcRegionRecommits) - recommits, Is.EqualTo(decommit ? 1 : 0));
            Assert.That(Interlocked.Read(ref Metrics.NoGcRegionEntries) - entries, Is.Zero, "a re-commit is not a payload's entry");
        }
    }

    public enum RecommitSkip { Cancelled, Shutdown, RegionHeld, Disallowed, NeverMode }

    // The next payload calls off a pending re-arm as it calls off the re-commit after a decommit.
    [Test]
    public async Task Recommit_is_skipped_when_called_off_or_not_needed([Values] RecommitSkip reason, [Values] bool rearm)
    {
        IGCStrategy strategy = ModeStrategy(rearm ? NoGcRegionMode.Guard : NoGcRegionMode.Always, collectionsPerDecommit: rearm ? -1 : 0);
        GCKeeper? keeper = null;
        IDisposable? held = null;
        RegionRuntime runtime = new()
        {
            BeforeCollect = () =>
            {
                switch (reason)
                {
                    case RecommitSkip.Cancelled: keeper!.CancelPendingGC(); break;
                    case RecommitSkip.Shutdown: keeper!.Dispose(); break;
                    case RecommitSkip.RegionHeld: held = keeper!.TryStartNoGCRegion(); break;
                    case RecommitSkip.Disallowed: strategy.CanStartNoGCRegion().Returns(false); break;
                    case RecommitSkip.NeverMode: strategy.NoGCRegionMode.Returns(NoGcRegionMode.Never); break;
                }
            }
        };
        List<IThreadPoolWorkItem> queued = [];
        using (keeper = new(strategy, NullLogManager.Instance, runtime, queued.Add, static (_, _) => Task.FromResult(true)))
        {
            // A payload has ended on a quiet node, so a re-arm would follow the collection.
            if (rearm) keeper.TryStartNoGCRegion().Dispose();
            strategy.GetForcedGCParams().Returns((GcLevel.Gen1, GcCompaction.No));
            await keeper.ScheduleGCInternal(throttle: false);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(runtime.Collections, Has.Count.EqualTo(1), "the collection itself is claimed before the payload arrives");
                Assert.That(runtime.Starts, Is.Zero);
            }

            strategy.GetForcedGCParams().Returns((GcLevel.NoGC, GcCompaction.No));
            held?.Dispose();
        }
    }

    [Test]
    public async Task Payload_entry_waits_for_a_running_recommit([Values] bool rearm)
    {
        using ManualResetEventSlim recommitting = new(false);
        using ManualResetEventSlim proceed = new(false);
        int starts = 0;
        RegionRuntime runtime = new()
        {
            BeforeStart = () =>
            {
                if (Interlocked.Increment(ref starts) != 1) return;
                recommitting.Set();
                proceed.Wait();
            }
        };
        IGCStrategy strategy = ModeStrategy(rearm ? NoGcRegionMode.Guard : NoGcRegionMode.Always, collectionsPerDecommit: rearm ? -1 : 0);
        List<IThreadPoolWorkItem> queued = [];
        using GCKeeper keeper = new(strategy, NullLogManager.Instance, runtime, queued.Add, static (_, _) => Task.FromResult(true));
        if (rearm) keeper.TryStartNoGCRegion().Dispose();
        int entries = queued.Count;
        strategy.GetForcedGCParams().Returns((GcLevel.Gen1, GcCompaction.No));

        Task collection = Task.Run(() => keeper.ScheduleGCInternal(throttle: false));
        Task worker = Task.CompletedTask;
        IDisposable? lease = null;
        try
        {
            try
            {
                Assert.That(recommitting.Wait(TimeSpan.FromSeconds(5)), Is.True);
                strategy.GetForcedGCParams().Returns((GcLevel.NoGC, GcCompaction.No));
                lease = keeper.TryStartNoGCRegion();
                Assert.That(queued, Has.Count.EqualTo(entries + 1), "the throwaway region holds no slot, so the payload is admitted");
                worker = Task.Run(queued[^1].Execute);
                await Task.Delay(100);
                Assert.That(worker.IsCompleted, Is.False, "the entry waits for the throwaway region to end");
            }
            finally
            {
                proceed.Set();
                await Task.WhenAll(collection, worker).WaitAsync(TimeSpan.FromSeconds(5));
            }

            using (Assert.EnterMultipleScope())
            {
                Assert.That(runtime.Operations, Is.EqualTo(new[] { "collect", "start", "end", "start" }));
                Assert.That(runtime.IsActive, Is.True, "the payload's region is not ended by the re-commit");
            }
        }
        finally
        {
            lease?.Dispose();
        }
    }

    // The throwaway entry moves the runtime's collection count as any entry does; a payload in processing meanwhile
    // must not take it for a collection.
    [Test]
    public async Task Recommit_during_processing_is_not_a_collection([Values] bool rearm)
    {
        IDisposable? lease = null;
        RegionRuntime runtime = new();
        IGCStrategy strategy = ModeStrategy(NoGcRegionMode.Guard, collectionsPerDecommit: rearm ? -1 : 0);
        List<IThreadPoolWorkItem> queued = [];
        using GCKeeper keeper = new(strategy, NullLogManager.Instance, runtime, queued.Add, static (_, _) => Task.FromResult(true));
        if (rearm) keeper.TryStartNoGCRegion().Dispose();
        runtime.BeforeStart = () => lease ??= keeper.TryStartNoGCRegion();
        long withCollection = Interlocked.Read(ref Metrics.NewPayloadsWithCollection);

        strategy.GetForcedGCParams().Returns((GcLevel.Gen1, GcCompaction.No));
        await keeper.ScheduleGCInternal(throttle: false);
        strategy.GetForcedGCParams().Returns((GcLevel.NoGC, GcCompaction.No));
        Assert.That(lease, Is.Not.Null, "the payload started as the throwaway region was entered");
        lease!.Dispose();

        Assert.That(Interlocked.Read(ref Metrics.NewPayloadsWithCollection) - withCollection, Is.Zero);
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

    /// <param name="level">NoGC keeps post-block collections, which run on their own, out of tests that drive them by hand.</param>
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

    private static GCKeeper CreateModeKeeper(IGCStrategy strategy, RegionRuntime runtime, List<IThreadPoolWorkItem> queued, ManualTime time, ILogManager? logManager = null) =>
        new(strategy, logManager ?? NullLogManager.Instance, runtime, queued.Add, static (_, _) => Task.FromResult(true), time);

    /// <summary>
    /// A payload that ends, then <paramref name="allocatedBetween"/> bytes allocated in the second until its post-block
    /// collection, which runs at gen1 for this block only.
    /// </summary>
    private static async Task Block(GCKeeper keeper, IGCStrategy strategy, RegionRuntime runtime, ManualTime time, long allocatedBetween = 0)
    {
        keeper.TryStartNoGCRegion().Dispose();
        time.Advance(TimeSpan.FromSeconds(1));
        runtime.AllocatedBytes += allocatedBetween;
        strategy.GetForcedGCParams().Returns((GcLevel.Gen1, GcCompaction.No));
        await keeper.ScheduleGCInternal(throttle: false);
        strategy.GetForcedGCParams().Returns((GcLevel.NoGC, GcCompaction.No));
    }

    // The entry is a stop-the-world on the payload's path that collects nothing; whether or not it is made, the
    // collection after the payload is what leaves the budget fresh for the next one, so it runs in every mode.
    [Test]
    public async Task Post_block_collection_and_decommit_run_in_every_mode([Values] NoGcRegionMode mode)
    {
        RegionRuntime runtime = new();
        List<IThreadPoolWorkItem> queued = [];
        IGCStrategy strategy = ModeStrategy(mode, level: GcLevel.Gen1, collectionsPerDecommit: 2);
        using GCKeeper keeper = new(strategy, NullLogManager.Instance, runtime, queued.Add, static (_, _) => Task.FromResult(true));
        long entries = Interlocked.Read(ref Metrics.NoGcRegionEntries);
        long skips = Interlocked.Read(ref Metrics.NoGcRegionSkips);

        // Nothing is armed yet, so the guard enters as Always does.
        bool enters = mode != NoGcRegionMode.Never;
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
        // The guard re-arms after it: nothing was allocated since the payload ended.
        Assert.That(() => runtime.Ends, Is.EqualTo((enters ? 1 : 0) + (mode == NoGcRegionMode.Guard ? 1 : 0)).After(5000, 10));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(runtime.Collections[0], Is.EqualTo((GcLevel.Gen1, GCCollectionMode.Forced, GcCompaction.No)));
            Assert.That(runtime.IsActive, Is.False);
        }

        // Payloads without a region still count towards the decommit, which comes on schedule.
        CountPayload(keeper, strategy);
        await keeper.ScheduleGCInternal(throttle: false);
        Assert.That(runtime.Collections[^1], Is.EqualTo((GcLevel.Gen2, GCCollectionMode.Aggressive, GcCompaction.Full)));
    }

    public enum Activity { Quiet, Busy, NoPayloadEnded }

    // A throwaway entry right after a collection on a quiet node is cheap; on a busy one it has to collect itself.
    [Test]
    public async Task Rearm_follows_a_quiet_block_in_guard_mode_only([Values] NoGcRegionMode mode, [Values] Activity activity)
    {
        RegionRuntime runtime = new();
        ManualTime time = new();
        List<IThreadPoolWorkItem> queued = [];
        IGCStrategy strategy = ModeStrategy(mode);
        using GCKeeper keeper = CreateModeKeeper(strategy, runtime, queued, time);
        long rearms = Interlocked.Read(ref Metrics.NoGcRegionRearms);
        long busy = Interlocked.Read(ref Metrics.NoGcRegionRearmsSkippedBusy);
        long recommits = Interlocked.Read(ref Metrics.NoGcRegionRecommits);
        long entries = Interlocked.Read(ref Metrics.NoGcRegionEntries);

        if (activity == Activity.NoPayloadEnded)
        {
            strategy.GetForcedGCParams().Returns((GcLevel.Gen1, GcCompaction.No));
            await keeper.ScheduleGCInternal(throttle: false);
        }
        else
        {
            // 8 MB in the second since the payload ended is still quiet.
            await Block(keeper, strategy, runtime, time, allocatedBetween: activity == Activity.Quiet ? 8 * Mb : 8 * Mb + 1);
        }

        bool rearmed = mode == NoGcRegionMode.Guard && activity == Activity.Quiet;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(runtime.Operations, Is.EqualTo(rearmed ? new[] { "collect", "start", "end" } : new[] { "collect" }));
            Assert.That(runtime.IsActive, Is.False);
            Assert.That(Interlocked.Read(ref Metrics.NoGcRegionRearms) - rearms, Is.EqualTo(rearmed ? 1 : 0));
            Assert.That(Interlocked.Read(ref Metrics.NoGcRegionRearmsSkippedBusy) - busy, Is.EqualTo(mode == NoGcRegionMode.Guard && !rearmed ? 1 : 0));
            Assert.That(Interlocked.Read(ref Metrics.NoGcRegionRecommits) - recommits, Is.Zero, "a re-arm is not a re-commit after a decommit");
            Assert.That(Interlocked.Read(ref Metrics.NoGcRegionEntries) - entries, Is.Zero, "a re-arm is not a payload's entry");
        }
    }

    public enum RearmCost { Fast, Slow, Collected, CollectingEntry }

    [Test]
    public async Task Slow_or_collecting_rearm_pauses_rearms_for_25_payloads([Values] RearmCost cost)
    {
        InterfaceLogger logger = Substitute.For<InterfaceLogger>();
        logger.IsInfo.Returns(true);
        RegionRuntime runtime = new();
        ManualTime time = new();
        List<IThreadPoolWorkItem> queued = [];
        IGCStrategy strategy = ModeStrategy(NoGcRegionMode.Guard);
        using GCKeeper keeper = CreateModeKeeper(strategy, runtime, queued, time, new OneLoggerLogManager(new ILogger(logger)));
        runtime.BeforeStart = () =>
        {
            runtime.BeforeStart = null;
            // 2 ms is not slow yet.
            time.Advance(TimeSpan.FromMilliseconds(2) + TimeSpan.FromTicks(cost == RearmCost.Slow ? 1 : 0));
        };
        runtime.AfterStart = () =>
        {
            runtime.AfterStart = null;
            // An entry that has to make room collects first: the index moves, and the count only once, as for any entry.
            if (cost is RearmCost.Collected or RearmCost.CollectingEntry) runtime.RunGC(moveCount: cost == RearmCost.Collected);
        };
        long rearms = Interlocked.Read(ref Metrics.NoGcRegionRearms);
        long backoffs = Interlocked.Read(ref Metrics.NoGcRegionRearmBackoffs);
        bool backsOff = cost != RearmCost.Fast;

        await Block(keeper, strategy, runtime, time);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Interlocked.Read(ref Metrics.NoGcRegionRearms) - rearms, Is.EqualTo(1));
            Assert.That(Interlocked.Read(ref Metrics.NoGcRegionRearmBackoffs) - backoffs, Is.EqualTo(backsOff ? 1 : 0));
            logger.Received(backsOff ? 1 : 0).Info(Arg.Is<string>(message => message.StartsWith("No-GC region re-arm took")));
        }

        for (int i = 1; i < 25; i++) await Block(keeper, strategy, runtime, time);
        Assert.That(Interlocked.Read(ref Metrics.NoGcRegionRearms) - rearms, Is.EqualTo(backsOff ? 1 : 25), "24 payloads later");
        await Block(keeper, strategy, runtime, time);
        Assert.That(Interlocked.Read(ref Metrics.NoGcRegionRearms) - rearms, Is.EqualTo(backsOff ? 2 : 26), "re-arms resume after 25 payloads");
    }

    public enum Arm { Rearm, RecommitAfterDecommit, PayloadEntry, Nothing }
    public enum CollectionSinceArm { None, Full, CountOnly, IndexOnly }

    // A throwaway entry leaves the region's budget in place until the next collection: a payload skips its own entry
    // while no collection has run since and at most the slack (32 MB by default) was allocated.
    [TestCase(Arm.Rearm, 0L, 0L, CollectionSinceArm.None, true)]
    [TestCase(Arm.Rearm, 32_000_000L, 0L, CollectionSinceArm.None, true, TestName = "{m}(exactly the default slack)")]
    [TestCase(Arm.Rearm, 32_000_001L, 0L, CollectionSinceArm.None, false)]
    [TestCase(Arm.Rearm, 100_000_000L, 100L, CollectionSinceArm.None, true, TestName = "{m}(exactly a configured slack)")]
    [TestCase(Arm.Rearm, 100_000_001L, 100L, CollectionSinceArm.None, false)]
    [TestCase(Arm.Rearm, 0L, 0L, CollectionSinceArm.Full, false)]
    [TestCase(Arm.Rearm, 0L, 0L, CollectionSinceArm.CountOnly, false, TestName = "{m}(background collection still running)")]
    [TestCase(Arm.Rearm, 0L, 0L, CollectionSinceArm.IndexOnly, false)]
    [TestCase(Arm.RecommitAfterDecommit, 0L, 0L, CollectionSinceArm.None, true, TestName = "{m}(armed by the re-commit on a busy node)")]
    [TestCase(Arm.PayloadEntry, 0L, 0L, CollectionSinceArm.None, false, TestName = "{m}(a payload's own entry does not arm)")]
    [TestCase(Arm.Nothing, 0L, 0L, CollectionSinceArm.None, false)]
    public async Task Guard_skips_the_entry_only_while_the_budget_is_armed(Arm arm, long allocatedSinceArm, long slackMb, CollectionSinceArm collection, bool skips)
    {
        RegionRuntime runtime = new();
        ManualTime time = new();
        List<IThreadPoolWorkItem> queued = [];
        IGCStrategy strategy = ModeStrategy(NoGcRegionMode.Guard, guardBytes: slackMb * Mb, collectionsPerDecommit: arm == Arm.RecommitAfterDecommit ? 0 : -1);
        using GCKeeper keeper = CreateModeKeeper(strategy, runtime, queued, time);
        switch (arm)
        {
            case Arm.Rearm:
                await Block(keeper, strategy, runtime, time);
                break;
            case Arm.RecommitAfterDecommit:
                await Block(keeper, strategy, runtime, time, allocatedBetween: 100 * Mb);
                break;
            case Arm.PayloadEntry:
                using (keeper.TryStartNoGCRegion()) queued[^1].Execute();
                break;
        }
        runtime.AllocatedBytes += allocatedSinceArm;
        if (collection != CollectionSinceArm.None)
        {
            runtime.RunGC(moveCount: collection != CollectionSinceArm.IndexOnly, moveIndex: collection != CollectionSinceArm.CountOnly);
        }
        int entries = queued.Count;
        long skipped = Interlocked.Read(ref Metrics.NoGcRegionSkips);

        using IDisposable lease = keeper.TryStartNoGCRegion();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(queued.Count - entries, Is.EqualTo(skips ? 0 : 1));
            Assert.That(Interlocked.Read(ref Metrics.NoGcRegionSkips) - skipped, Is.EqualTo(skips ? 1 : 0));
        }
    }

    [Test]
    public async Task Collections_during_processing_are_counted_without_own_entries([Values] NoGcRegionMode mode)
    {
        RegionRuntime runtime = new();
        ManualTime time = new();
        List<IThreadPoolWorkItem> queued = [];
        IGCStrategy strategy = ModeStrategy(mode);
        using GCKeeper keeper = CreateModeKeeper(strategy, runtime, queued, time);
        // Arms the guard, so its payloads below skip the region.
        await Block(keeper, strategy, runtime, time);
        long withCollection = Interlocked.Read(ref Metrics.NewPayloadsWithCollection);
        long misses = Interlocked.Read(ref Metrics.NoGcRegionGuardMisses);

        // A block with its region entered (always), or skipped (guard, never), and nothing collected.
        int entries = queued.Count;
        IDisposable quiet = keeper.TryStartNoGCRegion();
        if (queued.Count > entries) queued[^1].Execute();
        quiet.Dispose();
        quiet.Dispose();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Interlocked.Read(ref Metrics.NewPayloadsWithCollection) - withCollection, Is.Zero, "a region entry is not a collection");
            Assert.That(Interlocked.Read(ref Metrics.NoGcRegionGuardMisses) - misses, Is.Zero);
        }

        // A block during which the runtime collects.
        entries = queued.Count;
        IDisposable busy = keeper.TryStartNoGCRegion();
        if (queued.Count > entries) queued[^1].Execute();
        runtime.RunGC();
        busy.Dispose();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Interlocked.Read(ref Metrics.NewPayloadsWithCollection) - withCollection, Is.EqualTo(1));
            Assert.That(Interlocked.Read(ref Metrics.NoGcRegionGuardMisses) - misses, Is.EqualTo(mode == NoGcRegionMode.Guard ? 1 : 0),
                "only a payload the guard let run without a region is a miss");
        }
    }

    // An entry moves the collection count before the keeper sees it return. A payload whose window ends meanwhile must
    // not take it for a collection, nor for a guard miss, while a runtime collection alongside it still counts.
    [Test]
    public async Task Entry_in_flight_when_a_skipped_payload_ends_is_not_a_collection([Values] bool runtimeCollects)
    {
        RegionRuntime runtime = new();
        ManualTime time = new();
        List<IThreadPoolWorkItem> queued = [];
        IGCStrategy strategy = ModeStrategy(NoGcRegionMode.Guard);
        using GCKeeper keeper = CreateModeKeeper(strategy, runtime, queued, time);
        await Block(keeper, strategy, runtime, time);
        long withCollection = Interlocked.Read(ref Metrics.NewPayloadsWithCollection);
        long misses = Interlocked.Read(ref Metrics.NoGcRegionGuardMisses);

        int entries = queued.Count;
        IDisposable skipped = keeper.TryStartNoGCRegion();
        runtime.AllocatedBytes += 950 * Mb;
        using IDisposable entering = keeper.TryStartNoGCRegion();
        Assert.That(queued, Has.Count.EqualTo(entries + 1), "the first payload skipped the region while armed, the second enters past the slack");
        runtime.AfterStart = () =>
        {
            if (runtimeCollects) runtime.RunGC();
            skipped.Dispose();
        };
        queued[^1].Execute();

        int expected = runtimeCollects ? 1 : 0;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Interlocked.Read(ref Metrics.NewPayloadsWithCollection) - withCollection, Is.EqualTo(expected));
            Assert.That(Interlocked.Read(ref Metrics.NoGcRegionGuardMisses) - misses, Is.EqualTo(expected));
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
        public List<string> Operations { get; } = [];
        public bool CollectionSucceeds { get; set; } = true;
        public Action? BeforeCollect { get; init; }
        public bool Collect(GcLevel generation, GCCollectionMode mode, GcCompaction compacting)
        {
            BeforeCollect?.Invoke();
            Collections.Add((generation, mode, compacting));
            Operations.Add("collect");
            if (CollectionSucceeds) RunGC();
            return CollectionSucceeds;
        }
        public int CollectionCount { get; private set; }
        public long AllocatedBytes { get; set; }
        public long LastGcIndex { get; private set; }
        /// <summary>A collection the runtime runs: moves the count and the index, or one of them to model when each is updated.</summary>
        public void RunGC(bool moveCount = true, bool moveIndex = true)
        {
            if (moveCount) CollectionCount++;
            if (moveIndex) LastGcIndex++;
        }
        public Exception? EndFailure { get; init; }
        public Action? BeforeStart { get; set; }
        /// <summary>Runs inside the entry, after it has moved the count and before it returns.</summary>
        public Action? AfterStart { get; set; }
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
            Operations.Add("start");
            if (Throw) throw new InvalidOperationException("Another no-GC region is active.");
            // An entry moves the count and leaves the index (gc.cpp, update_collection_counts_for_no_gc).
            if (!Refuse) CollectionCount++;
            AfterStart?.Invoke();
            return IsActive = !Refuse;
        }
        public void End()
        {
            BeforeEnd?.Invoke();
            Ends++;
            Operations.Add("end");
            IsActive = false;
            if (EndFailure is not null) throw EndFailure;
        }
    }

    private sealed class ManualTime : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        public void Advance(TimeSpan by) => _ticks += by.Ticks;
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
