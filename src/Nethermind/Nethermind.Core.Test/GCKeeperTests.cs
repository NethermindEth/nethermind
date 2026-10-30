// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core.Memory;
using Nethermind.Core.Test.Threading;
using KeeperMetrics = Nethermind.Core.Memory.Metrics;
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
            (GcLevel.Gen1, GCCollectionMode.Forced, GcCompaction.No),
            (GcLevel.Gen2, GCCollectionMode.Aggressive, GcCompaction.Full),
            (GcLevel.Gen1, GCCollectionMode.Forced, GcCompaction.No),
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
            int before = runtime.Collections.Count;
            await keeper.ScheduleGCInternal(throttle: false);
            bool decommit = interval == 0 || (interval > 0 && i % interval == 0);
            // A decommit follows the payload's ordinary collection.
            Assert.That(runtime.Collections.Skip(before), Is.EqualTo(decommit ? new[] { Ordinary, Decommit } : new[] { Ordinary }), $"Payload {i}");
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
            // The ordinary collection runs before the idle wait; with no wait left the decommit covers it.
            Assert.That(runtime.Collections, Is.EqualTo(!decommit ? new[] { Ordinary }
                : postBlockDelayMs < 3000 ? new[] { Ordinary, Decommit } : new[] { Decommit }));
        }
    }

    [Test]
    public async Task Cancelled_idle_wait_retains_decommit_until_a_quiet_gap([Values] bool shutdown, [Values(1, 30)] int cancellations)
    {
        IGCStrategy strategy = Substitute.For<IGCStrategy>();
        strategy.CanStartNoGCRegion().Returns(true);
        strategy.CollectionsPerDecommit.Returns(1);
        strategy.GetForcedGCParams().Returns((GcLevel.Gen1, GcCompaction.No));
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
            Assert.That(runtime.Collections, Is.EqualTo(Enumerable.Repeat(Ordinary, i + 1)), "the ordinary collection ran before the idle wait");
            if (shutdown && i == cancellations - 1) keeper.Dispose();
            else keeper.CancelPendingGC();
            await pending.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(runtime.Collections, Does.Not.Contain(Decommit));
        }
        wait = false;
        await keeper.ScheduleGCInternal(throttle: false);
        if (shutdown)
        {
            Assert.That(runtime.Collections, Is.EqualTo(Enumerable.Repeat(Ordinary, cancellations)));
        }
        else
        {
            await keeper.ScheduleGCInternal(throttle: false);
            Assert.That(runtime.Collections.Skip(cancellations), Is.EqualTo(new[] { Ordinary, Decommit, Ordinary }));
        }
    }

    [Test]
    public async Task Collections_recommit_the_region_budget_off_the_payload_path([Values] bool decommit)
    {
        IGCStrategy strategy = Substitute.For<IGCStrategy>();
        strategy.CanStartNoGCRegion().Returns(true);
        strategy.GetForcedGCParams().Returns((GcLevel.Gen1, GcCompaction.No));
        strategy.CollectionsPerDecommit.Returns(decommit ? 0 : -1);
        RegionRuntime runtime = new();
        using GCKeeper keeper = new(strategy, NullLogManager.Instance, runtime, static _ => { }, static (_, _) => Task.FromResult(true));

        await keeper.ScheduleGCInternal(throttle: false);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(runtime.Collections, Is.EqualTo(decommit ? new[] { Ordinary, Decommit } : new[] { Ordinary }));
            Assert.That(runtime.Operations, Is.EqualTo(decommit
                ? new[] { "collect", "start", "end", "collect", "start", "end" }
                : new[] { "collect", "start", "end" }));
            Assert.That(runtime.IsActive, Is.False);
        }
    }

    // A collection the runtime takes to gen2 can go on in the background, and a region entry waits for it to end.
    [Test]
    public async Task Collection_taken_to_gen2_is_not_followed_by_a_recommit([Values] bool takenToGen2)
    {
        IGCStrategy strategy = CreateCadenceStrategy(0, adaptive: false);
        RegionRuntime runtime = new() { TakesToGen2 = takenToGen2 };
        using GCKeeper keeper = new(strategy, NullLogManager.Instance, runtime, static _ => { });
        KeeperCounters before = KeeperCounters.Read();

        await keeper.ScheduleGCInternal(throttle: false);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(runtime.Operations, Is.EqualTo(takenToGen2 ? new[] { "collect" } : new[] { "collect", "start", "end" }));
            Assert.That(KeeperCounters.Read().Since(before), Is.EqualTo(new KeeperCounters(Collections: 1, Recommits: takenToGen2 ? 0 : 1)));
        }
    }

    public enum RecommitSkip { Cancelled, Shutdown, RegionHeld, PayloadWithoutRegion, Disallowed }

    [Test]
    public async Task Recommit_is_skipped_when_called_off_or_not_needed([Values] RecommitSkip reason, [Values] bool decommit)
    {
        IGCStrategy strategy = Substitute.For<IGCStrategy>();
        strategy.CanStartNoGCRegion().Returns(true);
        strategy.GetForcedGCParams().Returns((GcLevel.Gen1, GcCompaction.No));
        strategy.CollectionsPerDecommit.Returns(decommit ? 0 : -1);
        GCKeeper? keeper = null;
        IDisposable? held = null;
        int collections = 0;
        RegionRuntime runtime = new()
        {
            BeforeCollect = () =>
            {
                // The decommit's collection after the ordinary one, or the ordinary one alone.
                if (++collections != (decommit ? 2 : 1)) return;
                switch (reason)
                {
                    case RecommitSkip.Cancelled: keeper!.CancelPendingGC(); break;
                    case RecommitSkip.Shutdown: keeper!.Dispose(); break;
                    case RecommitSkip.RegionHeld: held = keeper!.TryStartNoGCRegion(); break;
                    case RecommitSkip.PayloadWithoutRegion:
                        // A third overlapping payload gets no region of its own, and the two the region covered leave.
                        strategy.GetForcedGCParams().Returns((GcLevel.NoGC, GcCompaction.No));
                        IDisposable owner = keeper!.TryStartNoGCRegion();
                        IDisposable sharer = keeper.TryStartNoGCRegion();
                        held = keeper.TryStartNoGCRegion();
                        sharer.Dispose();
                        owner.Dispose();
                        break;
                    case RecommitSkip.Disallowed: strategy.CanStartNoGCRegion().Returns(false); break;
                }
            }
        };
        List<IThreadPoolWorkItem> queued = [];
        using (keeper = new(strategy, NullLogManager.Instance, runtime, queued.Add, static (_, _) => Task.FromResult(true)))
        {
            await keeper.ScheduleGCInternal(throttle: false);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(runtime.Collections, Is.EqualTo(decommit ? new[] { Ordinary, Decommit } : new[] { Ordinary }), "the collection itself is claimed before the payload arrives");
                Assert.That(runtime.Starts, Is.EqualTo(decommit ? 1 : 0), "only the re-commit after the ordinary collection before the decommit");
            }

            strategy.GetForcedGCParams().Returns((GcLevel.NoGC, GcCompaction.No));
            held?.Dispose();
        }
    }

    [Test]
    public async Task Payload_entry_waits_for_a_running_recommit([Values] bool decommit)
    {
        using ManualResetEventSlim recommitting = new(false);
        using ManualResetEventSlim proceed = new(false);
        // The re-commit after the decommit follows the one after the ordinary collection.
        int recommits = decommit ? 2 : 1;
        int starts = 0;
        RegionRuntime runtime = new()
        {
            BeforeStart = () =>
            {
                if (Interlocked.Increment(ref starts) != recommits) return;
                recommitting.Set();
                proceed.Wait();
            }
        };
        IGCStrategy strategy = Substitute.For<IGCStrategy>();
        strategy.CanStartNoGCRegion().Returns(true);
        strategy.GetForcedGCParams().Returns((GcLevel.Gen1, GcCompaction.No));
        strategy.CollectionsPerDecommit.Returns(decommit ? 0 : -1);
        List<IThreadPoolWorkItem> queued = [];
        using GCKeeper keeper = new(strategy, NullLogManager.Instance, runtime, queued.Add, static (_, _) => Task.FromResult(true));

        Task collection = Task.Run(() => keeper.ScheduleGCInternal(throttle: false));
        Thread? entry = null;
        IDisposable? lease = null;
        try
        {
            try
            {
                Assert.That(recommitting.Wait(TimeSpan.FromSeconds(5)), Is.True);
                strategy.GetForcedGCParams().Returns((GcLevel.NoGC, GcCompaction.No));
                lease = keeper.TryStartNoGCRegion();
                Assert.That(queued, Has.Count.EqualTo(1), "the re-commit holds no slot, so the payload is admitted");
                entry = new Thread(queued[0].Execute) { IsBackground = true };
                entry.Start();
                // Seen blocked, not merely not started yet: the entry waits on nothing before the runtime lock, so a
                // WaitSleepJoin state means it waits there; without that lock it would run to the end instead.
                Assert.That(SpinWait.SpinUntil(() => !entry.IsAlive || (entry.ThreadState & ThreadState.WaitSleepJoin) != 0, TimeSpan.FromSeconds(5)), Is.True);
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(entry.IsAlive, Is.True, "the entry waits for the throwaway region to end");
                    Assert.That(Volatile.Read(ref starts), Is.EqualTo(recommits), "the entry has not reached the runtime yet");
                }
            }
            finally
            {
                proceed.Set();
                await collection.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.That(entry?.Join(TimeSpan.FromSeconds(5)) ?? true, Is.True, "the entry finishes once the throwaway region has ended");
            }

            using (Assert.EnterMultipleScope())
            {
                Assert.That(runtime.Operations, Is.EqualTo(decommit
                    ? new[] { "collect", "start", "end", "collect", "start", "end", "start" }
                    : new[] { "collect", "start", "end", "start" }));
                Assert.That(runtime.IsActive, Is.True, "the payload's region is not ended by the re-commit");
            }
        }
        finally
        {
            lease?.Dispose();
        }
    }

    private static readonly (GcLevel, GCCollectionMode, GcCompaction) Decommit = (GcLevel.Gen2, GCCollectionMode.Aggressive, GcCompaction.Full);
    private static readonly (GcLevel, GCCollectionMode, GcCompaction) Ordinary = (GcLevel.Gen1, GCCollectionMode.Forced, GcCompaction.No);

    private static IGCStrategy CreatePressureStrategy(int interval, int loadPercent, int unconditional)
    {
        IGCStrategy strategy = Substitute.For<IGCStrategy>();
        strategy.CanStartNoGCRegion().Returns(true);
        strategy.GetForcedGCParams().Returns((GcLevel.Gen1, GcCompaction.No));
        strategy.CollectionsPerDecommit.Returns(interval);
        strategy.DecommitMemoryLoadPercent.Returns(loadPercent);
        strategy.CollectionsPerUnconditionalDecommit.Returns(unconditional);
        return strategy;
    }

    /// <summary>Counts payloads and runs each one's post-block collection; returns the 1-based payloads that decommitted.</summary>
    private static async Task<List<int>> RunPayloads(GCKeeper keeper, IGCStrategy strategy, RegionRuntime runtime, int payloads, Func<int, long>? memoryLoad = null)
    {
        List<int> decommits = [];
        for (int i = 1; i <= payloads; i++)
        {
            if (memoryLoad is not null) runtime.MemoryLoadBytes = memoryLoad(i);
            CountPayload(keeper, strategy);
            int before = runtime.Collections.Count;
            await keeper.ScheduleGCInternal(throttle: false);
            bool decommit = runtime.Collections[^1] == Decommit;
            // A decommit follows the payload's ordinary collection.
            Assert.That(runtime.Collections.Skip(before), Is.EqualTo(decommit ? new[] { Ordinary, Decommit } : new[] { Ordinary }), $"Payload {i}");
            if (decommit) decommits.Add(i);
        }
        return decommits;
    }

    [Test]
    public async Task Due_decommit_runs_only_under_memory_pressure([Values(30, 69, 70, 95)] long memoryLoadPercent)
    {
        IGCStrategy strategy = CreatePressureStrategy(interval: 25, loadPercent: 70, unconditional: 300);
        RegionRuntime runtime = new() { MemoryLoadBytes = memoryLoadPercent, TotalMemoryBytes = 100 };
        using GCKeeper keeper = new(strategy, NullLogManager.Instance, runtime, static _ => { }, static (_, _) => Task.FromResult(true));

        List<int> decommits = await RunPayloads(keeper, strategy, runtime, 100);

        bool pressure = memoryLoadPercent >= 70;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(decommits, Is.EqualTo(pressure ? new[] { 25, 50, 75, 100 } : Array.Empty<int>()));
            Assert.That(runtime.MemoryReads, Is.EqualTo(pressure ? 4 : 76), "memory is read only once a decommit is due, then after every payload until it runs");
        }
    }

    [Test]
    public async Task Skipped_decommit_stays_due_and_runs_once_pressure_appears()
    {
        IGCStrategy strategy = CreatePressureStrategy(interval: 25, loadPercent: 70, unconditional: 300);
        RegionRuntime runtime = new() { TotalMemoryBytes = 100 };
        using GCKeeper keeper = new(strategy, NullLogManager.Instance, runtime, static _ => { }, static (_, _) => Task.FromResult(true));

        // Pressure only at payload 41: the decommit due since 25 runs there, and the next is due 25 payloads later.
        List<int> decommits = await RunPayloads(keeper, strategy, runtime, 90, i => i == 41 || i >= 66 ? 80 : 20);

        Assert.That(decommits, Is.EqualTo(new[] { 41, 66 }));
    }

    [Test]
    public async Task Safety_net_decommits_without_pressure([Values(-1, 20, 3)] int unconditional)
    {
        IGCStrategy strategy = CreatePressureStrategy(interval: 5, loadPercent: 70, unconditional: unconditional);
        RegionRuntime runtime = new() { MemoryLoadBytes = 10, TotalMemoryBytes = 100 };
        using GCKeeper keeper = new(strategy, NullLogManager.Instance, runtime, static _ => { }, static (_, _) => Task.FromResult(true));

        List<int> decommits = await RunPayloads(keeper, strategy, runtime, 60);

        // -1 waits for pressure forever; a count below the interval cannot defer a due decommit.
        Assert.That(decommits, Is.EqualTo(unconditional switch
        {
            -1 => Array.Empty<int>(),
            20 => new[] { 20, 40, 60 },
            _ => Enumerable.Range(1, 12).Select(static i => i * 5).ToArray()
        }));
    }

    [Test]
    public async Task Disabled_decommit_never_runs_nor_reads_memory()
    {
        IGCStrategy strategy = CreatePressureStrategy(interval: -1, loadPercent: 70, unconditional: 1);
        RegionRuntime runtime = new() { MemoryLoadBytes = 100, TotalMemoryBytes = 100 };
        using GCKeeper keeper = new(strategy, NullLogManager.Instance, runtime, static _ => { }, static (_, _) => Task.FromResult(true));

        List<int> decommits = await RunPayloads(keeper, strategy, runtime, 50);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(decommits, Is.Empty);
            Assert.That(runtime.MemoryReads, Is.Zero);
        }
    }

    [Test]
    public async Task Without_threshold_or_known_memory_a_due_decommit_runs([Values] bool unknownMemory)
    {
        IGCStrategy strategy = CreatePressureStrategy(interval: 25, loadPercent: unknownMemory ? 70 : 0, unconditional: -1);
        RegionRuntime runtime = new() { MemoryLoadBytes = 0, TotalMemoryBytes = unknownMemory ? 0 : 100 };
        using GCKeeper keeper = new(strategy, NullLogManager.Instance, runtime, static _ => { }, static (_, _) => Task.FromResult(true));

        List<int> decommits = await RunPayloads(keeper, strategy, runtime, 50);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(decommits, Is.EqualTo(new[] { 25, 50 }));
            Assert.That(runtime.MemoryReads, Is.EqualTo(unknownMemory ? 2 : 0));
        }
    }

    [Test]
    public async Task Recommit_follows_each_collection_that_runs([Values] bool pressure)
    {
        IGCStrategy strategy = CreatePressureStrategy(interval: 0, loadPercent: 70, unconditional: -1);
        RegionRuntime runtime = new() { MemoryLoadBytes = pressure ? 90 : 10, TotalMemoryBytes = 100 };
        using GCKeeper keeper = new(strategy, NullLogManager.Instance, runtime, static _ => { }, static (_, _) => Task.FromResult(true));

        await keeper.ScheduleGCInternal(throttle: false);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(runtime.Collections, Is.EqualTo(pressure ? new[] { Ordinary, Decommit } : new[] { Ordinary }));
            Assert.That(runtime.Operations, Is.EqualTo(pressure
                ? new[] { "collect", "start", "end", "collect", "start", "end" }
                : new[] { "collect", "start", "end" }));
            Assert.That(runtime.IsActive, Is.False);
        }
    }

    [Test]
    public async Task Skipped_decommit_does_not_wait_for_an_idle_gap()
    {
        IGCStrategy strategy = CreatePressureStrategy(interval: 0, loadPercent: 70, unconditional: -1);
        strategy.PostBlockDelayMs.Returns(1500);
        RegionRuntime runtime = new() { MemoryLoadBytes = 10, TotalMemoryBytes = 100 };
        List<int> delays = [];
        using GCKeeper keeper = new(strategy, NullLogManager.Instance, runtime, static _ => { }, (milliseconds, _) =>
        {
            delays.Add(milliseconds);
            return Task.FromResult(true);
        });

        await keeper.ScheduleGCInternal(throttle: false);
        runtime.MemoryLoadBytes = 90;
        await keeper.ScheduleGCInternal(throttle: false);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(delays, Is.EqualTo(new[] { 1500, 1500, 1500 }), "the second, decommitting collection waits for the idle gap");
            Assert.That(runtime.Collections, Is.EqualTo(new[] { Ordinary, Ordinary, Decommit }));
        }
    }

    // Payloads every 2 s never leave a decommit its 3 s idle gap: from the unconditional count on it is due after every
    // payload and the next payload cuts its idle wait short. Each payload still gets its ordinary collection.
    [Test]
    public async Task Decommit_that_never_gets_its_idle_gap_does_not_stop_ordinary_collections()
    {
        IGCStrategy strategy = CreatePressureStrategy(interval: 25, loadPercent: 70, unconditional: 300);
        strategy.PostBlockDelayMs.Returns(250);
        RegionRuntime runtime = new() { MemoryLoadBytes = 10, TotalMemoryBytes = 100 };
        List<int> idleWaits = [];
        using GCKeeper keeper = new(strategy, NullLogManager.Instance, runtime, static _ => { }, (milliseconds, _) =>
        {
            if (milliseconds == 250) return Task.FromResult(true);
            idleWaits.Add(milliseconds);
            return Task.FromResult(false);
        });

        KeeperCounters before = KeeperCounters.Read();
        for (int i = 1; i <= 400; i++)
        {
            CountPayload(keeper, strategy);
            await keeper.ScheduleGCInternal(throttle: false);
        }

        KeeperCounters counted = KeeperCounters.Read().Since(before);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(runtime.Collections, Is.EqualTo(Enumerable.Repeat(Ordinary, 400)));
            Assert.That(idleWaits, Is.EqualTo(Enumerable.Repeat(2750, 101)), "the decommit stays due from payload 300 on");
            Assert.That(counted, Is.EqualTo(new KeeperCounters(Collections: 400, IdleWaitsCancelled: 101, Recommits: 400)));
        }
    }

    [Test]
    public async Task Spacing_does_not_hold_back_a_decommit([Values] bool ordinaryThrottled)
    {
        IGCStrategy strategy = CreatePressureStrategy(interval: ordinaryThrottled ? 1 : 0, loadPercent: 0, unconditional: -1);
        RegionRuntime runtime = new();
        using GCKeeper keeper = new(strategy, NullLogManager.Instance, runtime, static _ => { }, static (_, _) => Task.FromResult(true));

        // Either the decommit's own ordinary collection has just run, or a collection before it throttles that one.
        KeeperCounters before = KeeperCounters.Read();
        if (ordinaryThrottled) await keeper.ScheduleGCInternal(throttle: true);
        CountPayload(keeper, strategy);
        await keeper.ScheduleGCInternal(throttle: true);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(runtime.Collections, Is.EqualTo(new[] { Ordinary, Decommit }));
            Assert.That(KeeperCounters.Read().Since(before), Is.EqualTo(new KeeperCounters(Collections: 1, Throttled: ordinaryThrottled ? 1 : 0, Decommits: 1, Recommits: 2)));
        }
    }

    private static ILogManager CreateInfoLogManager(List<string> infos)
    {
        InterfaceLogger logger = Substitute.For<InterfaceLogger>();
        logger.IsInfo.Returns(true);
        logger.When(static l => l.Info(Arg.Any<string>())).Do(call => infos.Add(call.Arg<string>()));
        return new OneLoggerLogManager(new ILogger(logger));
    }

    [Test]
    public async Task Decision_is_logged_when_it_changes_not_after_every_payload()
    {
        List<string> infos = [];
        IGCStrategy strategy = CreatePressureStrategy(interval: 5, loadPercent: 70, unconditional: 15);
        RegionRuntime runtime = new() { TotalMemoryBytes = 100 };
        using GCKeeper keeper = new(strategy, CreateInfoLogManager(infos), runtime, static _ => { }, static (_, _) => Task.FromResult(true));

        // No pressure up to 40: skips at 5-14, safety net at 15, skips at 20-29, safety net at 30; pressure from 41 on.
        List<int> decommits = await RunPayloads(keeper, strategy, runtime, 50, static i => i > 40 ? 90 : 10);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(decommits, Is.EqualTo(new[] { 15, 30, 41, 46 }));
            Assert.That(infos, Has.Count.EqualTo(6), string.Join(Environment.NewLine, infos));
            Assert.That(infos.Select(static m => m.Split(':')[0]), Is.EqualTo(new[]
            {
                "GC decommit deferred", "GC decommit", "GC decommit deferred", "GC decommit", "GC decommit deferred", "GC decommit"
            }));
            Assert.That(infos[^1], Does.Contain("at or above 70%"));
        }
    }

    [Test]
    public async Task Decommit_cancelled_in_its_idle_wait_is_not_logged_as_a_release()
    {
        List<string> infos = [];
        IGCStrategy strategy = CreatePressureStrategy(interval: 0, loadPercent: 70, unconditional: -1);
        RegionRuntime runtime = new() { MemoryLoadBytes = 90, TotalMemoryBytes = 100 };
        bool cancelIdleWait = true;
        using GCKeeper keeper = new(strategy, CreateInfoLogManager(infos), runtime, static _ => { }, (_, _) => Task.FromResult(!cancelIdleWait));

        // A new payload cancels the idle wait of a decommit already decided under pressure.
        await keeper.ScheduleGCInternal(throttle: false);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(runtime.Collections, Is.EqualTo(new[] { Ordinary }), "only the ordinary collection ran");
            Assert.That(infos, Is.Empty, "nothing was released");
        }

        // The retry, under the same decision, releases memory and only then logs it, once.
        cancelIdleWait = false;
        await keeper.ScheduleGCInternal(throttle: false);
        await keeper.ScheduleGCInternal(throttle: false);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(runtime.Collections, Is.EqualTo(new[] { Ordinary, Ordinary, Decommit, Ordinary, Decommit }));
            Assert.That(infos, Has.Count.EqualTo(1), string.Join(Environment.NewLine, infos));
            Assert.That(infos[0], Does.StartWith("GC decommit: memory load 90% of").And.Contain("releasing memory"));
        }
    }

    [Test]
    public async Task Decommit_whose_collection_is_declined_is_not_logged_until_one_runs()
    {
        List<string> infos = [];
        IGCStrategy strategy = CreatePressureStrategy(interval: 0, loadPercent: 70, unconditional: -1);
        RegionRuntime runtime = new() { MemoryLoadBytes = 90, TotalMemoryBytes = 100, CollectionSucceeds = false };
        using GCKeeper keeper = new(strategy, CreateInfoLogManager(infos), runtime, static _ => { }, static (_, _) => Task.FromResult(true));

        // The scheduler declines the collection (a payload paused it): nothing was released.
        await keeper.ScheduleGCInternal(throttle: false);
        Assert.That(infos, Is.Empty, "nothing was released");

        runtime.CollectionSucceeds = true;
        await keeper.ScheduleGCInternal(throttle: false);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(runtime.Collections, Is.EqualTo(new[] { Ordinary, Decommit, Ordinary, Decommit }));
            Assert.That(infos, Has.Count.EqualTo(1), string.Join(Environment.NewLine, infos));
        }
    }

    // A host with 67,416,633,344 bytes: the runtime records a 70% load as (ulong)(0.70 * total) bytes and reports the
    // 90% high memory load threshold as (ulong)(0.90 * total) bytes (gc.cpp GCHeap::GetMemoryInfo). The total derived
    // from them is one byte short, so an exact byte comparison would read the recorded 70% as just below 70%.
    [Test]
    public async Task Load_recorded_at_exactly_the_threshold_percent_decommits()
    {
        GcMemoryLoad load = GcMemoryLoad.FromRecorded(47_191_643_340, 60_674_970_009, new Dictionary<string, object> { ["GCHighMemPercent"] = 90L });
        IGCStrategy strategy = CreatePressureStrategy(interval: 0, loadPercent: 70, unconditional: -1);
        RegionRuntime runtime = new() { MemoryLoadBytes = load.MemoryLoadBytes, TotalMemoryBytes = load.TotalMemoryBytes };
        using GCKeeper keeper = new(strategy, NullLogManager.Instance, runtime, static _ => { }, static (_, _) => Task.FromResult(true));

        await keeper.ScheduleGCInternal(throttle: false);

        Assert.That(runtime.Collections, Is.EqualTo(new[] { Ordinary, Decommit }), $"total {load.TotalMemoryBytes}");
    }

    [Test]
    public async Task Load_just_below_the_threshold_percent_does_not_decommit()
    {
        IGCStrategy strategy = CreatePressureStrategy(interval: 0, loadPercent: 70, unconditional: -1);
        RegionRuntime runtime = new() { MemoryLoadBytes = 696, TotalMemoryBytes = 1000 };
        using GCKeeper keeper = new(strategy, NullLogManager.Instance, runtime, static _ => { }, static (_, _) => Task.FromResult(true));

        await keeper.ScheduleGCInternal(throttle: false);

        Assert.That(runtime.Collections, Does.Not.Contain(Decommit));
    }

    /// <summary>A strategy written against <see cref="IGCStrategy"/> before it had the memory-pressure members.</summary>
    private sealed class CadenceOnlyStrategy(int collectionsPerDecommit) : IGCStrategy
    {
        public (GcLevel Generation, GcCompaction Compacting) ForcedGCParams { get; set; } = (GcLevel.Gen1, GcCompaction.No);
        public int CollectionsPerDecommit => collectionsPerDecommit;
        public int PostBlockDelayMs => 0;
        public bool CanStartNoGCRegion() => true;
        public (GcLevel Generation, GcCompaction Compacting) GetForcedGCParams() => ForcedGCParams;
    }

    [Test]
    public async Task Strategy_without_the_pressure_members_decommits_whenever_due([Values(10, 95)] long memoryLoadPercent)
    {
        CadenceOnlyStrategy strategy = new(collectionsPerDecommit: 5);
        RegionRuntime runtime = new() { MemoryLoadBytes = memoryLoadPercent, TotalMemoryBytes = 100 };
        using GCKeeper keeper = new(strategy, NullLogManager.Instance, runtime, static _ => { }, static (_, _) => Task.FromResult(true));

        List<int> decommits = [];
        for (int i = 1; i <= 20; i++)
        {
            strategy.ForcedGCParams = (GcLevel.NoGC, GcCompaction.No);
            using (keeper.TryStartNoGCRegion()) { }
            strategy.ForcedGCParams = (GcLevel.Gen1, GcCompaction.No);
            await keeper.ScheduleGCInternal(throttle: false);
            if (runtime.Collections[^1] == Decommit) decommits.Add(i);
        }

        IGCStrategy defaults = strategy;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(defaults.DecommitMemoryLoadPercent, Is.Zero);
            Assert.That(defaults.CollectionsPerUnconditionalDecommit, Is.EqualTo(-1));
            Assert.That(decommits, Is.EqualTo(new[] { 5, 10, 15, 20 }));
            Assert.That(runtime.MemoryReads, Is.Zero);
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
        public List<string> Operations { get; } = [];
        public bool CollectionSucceeds { get; set; } = true;
        /// <summary>Takes every requested collection to gen2, as the runtime does once gen2's budget is used up.</summary>
        public bool TakesToGen2 { get; init; }
        public int Gen2CollectionCount { get; private set; }
        public Action? BeforeCollect { get; init; }
        public bool Collect(GcLevel generation, GCCollectionMode mode, GcCompaction compacting)
        {
            BeforeCollect?.Invoke();
            Collections.Add((generation, mode, compacting));
            Operations.Add("collect");
            if (generation == GcLevel.Gen2 || TakesToGen2) Gen2CollectionCount++;
            return CollectionSucceeds;
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
            Operations.Add("start");
            if (Throw) throw new InvalidOperationException("Another no-GC region is active.");
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
        public long MemoryLoadBytes { get; set; }
        public long TotalMemoryBytes { get; set; } = 100;
        public int MemoryReads { get; private set; }
        public GcMemoryLoad GetMemoryLoad()
        {
            MemoryReads++;
            return new(MemoryLoadBytes, TotalMemoryBytes);
        }
    }

    private static IGCStrategy CreateCadenceStrategy(int postBlockDelayMs, bool adaptive)
    {
        IGCStrategy strategy = Substitute.For<IGCStrategy>();
        strategy.CanStartNoGCRegion().Returns(true);
        strategy.GetForcedGCParams().Returns((GcLevel.Gen1, GcCompaction.No));
        strategy.CollectionsPerDecommit.Returns(-1);
        strategy.PostBlockDelayMs.Returns(postBlockDelayMs);
        strategy.AdaptivePostBlockDelay.Returns(adaptive);
        return strategy;
    }

    /// <summary>
    /// Delivers payloads on a manual clock at a fixed interval, each in processing for a fixed time, and runs the
    /// throttled post-block collection each one schedules. A post-block delay longer than the idle time left is cut
    /// short by the next payload, as its engine API call cancels the pending collection.
    /// </summary>
    private sealed class CadenceRun
    {
        private long _idleLeftMs;
        public ManualTimeProvider Time { get; } = new();
        /// <summary>The post-block delays the keeper asked for, one per payload.</summary>
        public List<int> Delays { get; } = [];

        public Task<bool> Delay(int milliseconds, CancellationToken _)
        {
            Delays.Add(milliseconds);
            bool completed = milliseconds <= _idleLeftMs;
            long waitedMs = completed ? milliseconds : _idleLeftMs;
            Time.Advance(TimeSpan.FromMilliseconds(waitedMs));
            _idleLeftMs -= waitedMs;
            return Task.FromResult(completed);
        }

        /// <returns>The 1-based payloads after which a collection ran.</returns>
        public async Task<List<int>> Run(GCKeeper keeper, IGCStrategy strategy, RegionRuntime runtime, int payloads, int intervalMs, int processingMs)
        {
            List<int> collected = [];
            for (int i = 1; i <= payloads; i++)
            {
                strategy.GetForcedGCParams().Returns((GcLevel.NoGC, GcCompaction.No));
                IDisposable lease = keeper.TryStartNoGCRegion();
                Time.Advance(TimeSpan.FromMilliseconds(processingMs));
                lease.Dispose();
                strategy.GetForcedGCParams().Returns((GcLevel.Gen1, GcCompaction.No));

                _idleLeftMs = intervalMs - processingMs;
                int before = runtime.Collections.Count;
                await keeper.ScheduleGCInternal(throttle: true);
                if (runtime.Collections.Count > before) collected.Add(i);
                Time.Advance(TimeSpan.FromMilliseconds(_idleLeftMs));
            }
            return collected;
        }
    }

    // The window holds 16 samples: from payload 17 on, the keeper times its collection by the cadence.
    private const int CadencePayloads = 40;
    private const int FirstAdaptedPayload = PayloadCadence.Samples + 1;

    public enum Cadence { Ms200, Seconds2, Seconds12 }

    [Test]
    public async Task Post_block_delay_and_spacing_follow_the_payload_cadence([Values] Cadence cadence)
    {
        (int intervalMs, int processingMs, int slotDelayMs, int adaptedDelayMs) = cadence switch
        {
            // 160 ms idle: a quarter of it, so the collection runs before the next payload.
            Cadence.Ms200 => (200, 40, 250, 40),
            // 1.8 s idle: a quarter would be longer than the slot's delay, which stays.
            Cadence.Seconds2 => (2_000, 200, 250, 250),
            Cadence.Seconds12 => (12_000, 500, 1_500, 1_500),
            _ => throw new ArgumentOutOfRangeException(nameof(cadence))
        };
        IGCStrategy strategy = CreateCadenceStrategy(slotDelayMs, adaptive: true);
        RegionRuntime runtime = new();
        CadenceRun run = new();
        using GCKeeper keeper = new(strategy, NullLogManager.Instance, runtime, static _ => { }, run.Delay, run.Time);

        KeeperCounters before = KeeperCounters.Read();
        List<int> collected = await run.Run(keeper, strategy, runtime, CadencePayloads, intervalMs, processingMs);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(KeeperCounters.Read().Since(before), Is.EqualTo(new KeeperCounters(
                Collections: collected.Count,
                Cancelled: cadence == Cadence.Ms200 ? PayloadCadence.Samples : 0,
                Throttled: cadence == Cadence.Seconds2 ? PayloadCadence.Samples / 2 : 0,
                Recommits: collected.Count)));
            Assert.That(KeeperMetrics.GcKeeperPostBlockDelayMs, Is.EqualTo(adaptedDelayMs));
            Assert.That(KeeperMetrics.GcKeeperMinMsBetweenCollections, Is.EqualTo(Math.Min(3_000, intervalMs / 2)));
            Assert.That(KeeperMetrics.GcKeeperMedianIdleGapMs, Is.EqualTo(intervalMs - processingMs));
            Assert.That(KeeperMetrics.GcKeeperMedianPayloadIntervalMs, Is.EqualTo(intervalMs));
            Assert.That(run.Delays.Take(PayloadCadence.Samples), Is.All.EqualTo(slotDelayMs), "the slot's delay until the window is full");
            Assert.That(run.Delays.Skip(PayloadCadence.Samples), Is.All.EqualTo(adaptedDelayMs));
            // At 200 ms the slot's delay never completes; at 2 s the 3 s spacing lets every other payload collect.
            Assert.That(collected.Count(static i => i < FirstAdaptedPayload), Is.EqualTo(cadence switch
            {
                Cadence.Ms200 => 0,
                Cadence.Seconds2 => 8,
                _ => 16
            }));
            Assert.That(collected.Where(static i => i >= FirstAdaptedPayload),
                Is.EqualTo(Enumerable.Range(FirstAdaptedPayload, CadencePayloads - PayloadCadence.Samples)), "a collection after every payload");
        }
    }

    [Test]
    public async Task Back_to_back_payloads_keep_the_configured_timing([Values] bool explicitZeroDelay)
    {
        // A 45 ms payload every 50 ms leaves 5 ms of idle time: catch-up or replay, not a chain's cadence.
        IGCStrategy strategy = CreateCadenceStrategy(explicitZeroDelay ? 0 : 250, adaptive: !explicitZeroDelay);
        RegionRuntime runtime = new();
        CadenceRun run = new();
        using GCKeeper keeper = new(strategy, NullLogManager.Instance, runtime, static _ => { }, run.Delay, run.Time);

        List<int> collected = await run.Run(keeper, strategy, runtime, 100, 50, 45);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(run.Delays, Is.EqualTo(explicitZeroDelay ? Array.Empty<int>() : Enumerable.Repeat(250, 100).ToArray()));
            // Without a delay the 3 s spacing still holds: payload 1, then the first one more than 3 s later.
            Assert.That(collected, Is.EqualTo(explicitZeroDelay ? new[] { 1, 62 } : Array.Empty<int>()));
        }
    }

    [Test]
    public async Task Explicit_post_block_delay_is_kept_while_the_spacing_adapts()
    {
        IGCStrategy strategy = CreateCadenceStrategy(100, adaptive: false);
        RegionRuntime runtime = new();
        CadenceRun run = new();
        using GCKeeper keeper = new(strategy, NullLogManager.Instance, runtime, static _ => { }, run.Delay, run.Time);

        List<int> collected = await run.Run(keeper, strategy, runtime, CadencePayloads, 200, 40);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(run.Delays, Is.EqualTo(Enumerable.Repeat(100, CadencePayloads)));
            Assert.That(collected.Where(static i => i >= FirstAdaptedPayload),
                Is.EqualTo(Enumerable.Range(FirstAdaptedPayload, CadencePayloads - PayloadCadence.Samples)));
        }
    }

    // A payload that starts while another is still in processing leaves no idle gap. Here every other payload starts
    // while the one before it is still in processing: half the gaps are 0, the other half 150 ms.
    [Test]
    public async Task Overlapping_payload_leaves_no_idle_gap()
    {
        IGCStrategy strategy = CreateCadenceStrategy(250, adaptive: true);
        RegionRuntime runtime = new();
        CadenceRun run = new();
        using GCKeeper keeper = new(strategy, NullLogManager.Instance, runtime, static _ => { }, run.Delay, run.Time);

        strategy.GetForcedGCParams().Returns((GcLevel.NoGC, GcCompaction.No));
        // The first payload has no gap before it: 17 gaps, the window keeps the last 16.
        for (int pair = 0; pair <= PayloadCadence.Samples / 2; pair++)
        {
            IDisposable first = keeper.TryStartNoGCRegion();
            run.Time.Advance(TimeSpan.FromMilliseconds(200));
            IDisposable second = keeper.TryStartNoGCRegion();
            run.Time.Advance(TimeSpan.FromMilliseconds(10));
            first.Dispose();
            run.Time.Advance(TimeSpan.FromMilliseconds(40));
            second.Dispose();
            run.Time.Advance(TimeSpan.FromMilliseconds(150));
        }
        strategy.GetForcedGCParams().Returns((GcLevel.Gen1, GcCompaction.No));
        await keeper.ScheduleGCInternal(throttle: false);

        // A median idle gap of (0 + 150) / 2 = 75 ms, a quarter of it the delay.
        Assert.That(run.Delays, Is.EqualTo(new[] { 18 }));
    }

    [Test]
    public async Task Never_mode_keeps_the_keeper_without_entering_a_region([Values] NoGcRegionMode mode)
    {
        // Due after every payload, so a payload that did not count would leave the decommit out.
        IGCStrategy strategy = CreatePressureStrategy(interval: 1, loadPercent: 0, unconditional: -1);
        strategy.NoGcRegionOnNewPayload.Returns(mode);
        RegionRuntime runtime = new();
        List<IThreadPoolWorkItem> queued = [];
        using GCKeeper keeper = new(strategy, NullLogManager.Instance, runtime, queued.Add, static (_, _) => Task.FromResult(true));
        KeeperCounters before = KeeperCounters.Read();

        strategy.GetForcedGCParams().Returns((GcLevel.NoGC, GcCompaction.No));
        using (keeper.TryStartNoGCRegion())
        {
            foreach (IThreadPoolWorkItem entry in queued) entry.Execute();
            bool gateFree = GCScheduler.MarkGCPaused();
            if (gateFree) GCScheduler.MarkGCResumed();
            Assert.That(gateFree, Is.False, "the payload holds off scheduled collections");
        }
        strategy.GetForcedGCParams().Returns((GcLevel.Gen1, GcCompaction.No));
        await keeper.ScheduleGCInternal(throttle: false);

        bool always = mode == NoGcRegionMode.Always;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(queued, Has.Count.EqualTo(always ? 1 : 0));
            Assert.That(runtime.Collections, Is.EqualTo(new[] { Ordinary, Decommit }));
            // The payload's region, then a re-commit after each collection.
            Assert.That(runtime.Operations, Is.EqualTo(always
                ? new[] { "start", "end", "collect", "start", "end", "collect", "start", "end" }
                : new[] { "collect", "collect" }));
            Assert.That(KeeperCounters.Read().Since(before), Is.EqualTo(new KeeperCounters(Collections: 1, Decommits: 1, Recommits: always ? 2 : 0)));
        }
    }

    // A payload that starts while another is in processing finds the scheduler paused and does not hold it once the
    // other one lets go; without a region of its own nothing but the keeper keeps the collection out of it.
    [Test]
    public async Task Collection_waits_for_payloads_still_in_processing([Values] NoGcRegionMode mode)
    {
        IGCStrategy strategy = CreateCadenceStrategy(0, adaptive: false);
        strategy.NoGcRegionOnNewPayload.Returns(mode);
        RegionRuntime runtime = new();
        using GCKeeper keeper = new(strategy, NullLogManager.Instance, runtime, static _ => { });
        KeeperCounters before = KeeperCounters.Read();

        strategy.GetForcedGCParams().Returns((GcLevel.NoGC, GcCompaction.No));
        IDisposable first = keeper.TryStartNoGCRegion();
        IDisposable second = keeper.TryStartNoGCRegion();
        first.Dispose();
        strategy.GetForcedGCParams().Returns((GcLevel.Gen1, GcCompaction.No));
        await keeper.ScheduleGCInternal(throttle: false);
        Assert.That(runtime.Collections, Is.Empty, "the second payload is still in processing");

        strategy.GetForcedGCParams().Returns((GcLevel.NoGC, GcCompaction.No));
        second.Dispose();
        strategy.GetForcedGCParams().Returns((GcLevel.Gen1, GcCompaction.No));
        await keeper.ScheduleGCInternal(throttle: false);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(runtime.Collections, Is.EqualTo(new[] { Ordinary }));
            Assert.That(KeeperCounters.Read().Since(before), Is.EqualTo(new KeeperCounters(Collections: 1, Skipped: 1, Recommits: mode == NoGcRegionMode.Always ? 1 : 0)));
        }
    }

    [Test]
    public async Task Declined_and_cancelled_collections_are_counted([Values] bool cancelled)
    {
        IGCStrategy strategy = CreateCadenceStrategy(1, adaptive: false);
        RegionRuntime runtime = new() { CollectionSucceeds = false };
        using GCKeeper keeper = new(strategy, NullLogManager.Instance, runtime, static _ => { }, (_, _) => Task.FromResult(!cancelled));
        KeeperCounters before = KeeperCounters.Read();

        await keeper.ScheduleGCInternal(throttle: false);

        Assert.That(KeeperCounters.Read().Since(before), Is.EqualTo(cancelled ? new KeeperCounters(Cancelled: 1) : new KeeperCounters(Skipped: 1)));
    }

    [Test]
    public void Reset_gives_every_keeper_metric_its_start_value()
    {
        // Every field, so a metric added later without a reset fails here.
        FieldInfo[] fields = typeof(KeeperMetrics).GetFields(BindingFlags.Public | BindingFlags.Static);
        foreach (FieldInfo field in fields) field.SetValue(null, 12_345L);

        KeeperMetrics.ResetGcKeeper();

        using (Assert.EnterMultipleScope())
        {
            foreach (FieldInfo field in fields)
            {
                long expected = field.Name is nameof(KeeperMetrics.GcKeeperMedianIdleGapMs) or nameof(KeeperMetrics.GcKeeperMedianPayloadIntervalMs) ? -1 : 0;
                Assert.That(field.GetValue(null), Is.EqualTo(expected), field.Name);
            }
        }
    }

    /// <summary>The keeper's counters at one moment, so that a test reads what it moved.</summary>
    private readonly record struct KeeperCounters(long Collections = 0, long Cancelled = 0, long Throttled = 0, long Skipped = 0,
        long Decommits = 0, long IdleWaitsCancelled = 0, long Recommits = 0)
    {
        public static KeeperCounters Read() => new(
            Interlocked.Read(ref KeeperMetrics.GcKeeperPostBlockCollections),
            Interlocked.Read(ref KeeperMetrics.GcKeeperPostBlockCancelled),
            Interlocked.Read(ref KeeperMetrics.GcKeeperPostBlockThrottled),
            Interlocked.Read(ref KeeperMetrics.GcKeeperPostBlockSkipped),
            Interlocked.Read(ref KeeperMetrics.GcKeeperDecommits),
            Interlocked.Read(ref KeeperMetrics.GcKeeperDecommitIdleWaitsCancelled),
            Interlocked.Read(ref KeeperMetrics.NoGcRegionRecommits));

        public KeeperCounters Since(KeeperCounters before) => new(Collections - before.Collections, Cancelled - before.Cancelled,
            Throttled - before.Throttled, Skipped - before.Skipped, Decommits - before.Decommits,
            IdleWaitsCancelled - before.IdleWaitsCancelled, Recommits - before.Recommits);
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
