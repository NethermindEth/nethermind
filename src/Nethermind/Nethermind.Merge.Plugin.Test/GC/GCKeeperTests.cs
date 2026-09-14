// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.GC;
using NUnit.Framework;

namespace Nethermind.Merge.Plugin.Test.GC;

// The keeper drives the process-wide GCScheduler guard and payload counter, so fixtures cannot interleave.
[TestFixture]
[NonParallelizable]
public class GCKeeperTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);
    // Ten settle windows: long enough for a sweep that should have stood down to show up.
    private static readonly TimeSpan SettleAllowance = TimeSpan.FromSeconds(1);
    private const int CompactionDelayMs = 200;
    // Far beyond any test, so only the per-block sweep is observable unless a test asks for compaction.
    private const int NeverWithinTestMs = 60_000;

    private static RegionStrategy CompactingStrategy() =>
        new(allowRegions: true, sweep: GcLevel.Gen1, compaction: GcCompaction.Yes, postBlockDelayMs: CompactionDelayMs);

    [Test]
    public void Request_path_returns_while_region_entry_is_still_blocked()
    {
        FakeRuntime runtime = new();
        runtime.RegionEntryGate.Reset();
        using GCKeeper keeper = new(new RegionStrategy(allowRegions: true), LimboLogs.Instance, runtime);

        // With entry blocked (a background GC in flight), a request path that entered the region itself would hang here.
        using (keeper.TryStartNoGCRegion())
        {
            Assert.That(runtime.RegionsStarted, Is.Zero);
            runtime.RegionEntryGate.Set();
            Assert.That(runtime.RegionStarted.Wait(Patience), "the keeper never entered the region");
        }

        Assert.That(runtime.RegionEnded.Wait(Patience), "the region outlived its request");
    }

    [Test]
    public void Request_released_while_entry_is_blocked_ends_the_region_at_once_and_sweeps()
    {
        FakeRuntime runtime = new();
        runtime.RegionEntryGate.Reset();
        using GCKeeper keeper = new(new RegionStrategy(allowRegions: true), LimboLogs.Instance, runtime);

        IDisposable region = keeper.TryStartNoGCRegion();
        Assert.That(runtime.EntryRequested.Wait(Patience), "the keeper never tried to enter the region");
        region.Dispose();
        runtime.RegionEntryGate.Set();

        Assert.That(runtime.RegionEnded.Wait(Patience), "a region entered for a finished request was left active");
        Assert.That(runtime.Collections.Wait(Patience), "the block's garbage was never swept");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(runtime.RegionsStarted, Is.EqualTo(1));
            Assert.That(runtime.RegionsEnded, Is.EqualTo(1));
        }
    }

    [Test]
    public void Burst_of_requests_drained_in_one_pass_is_swept_once()
    {
        FakeRuntime runtime = new();
        runtime.RegionEntryGate.Reset();
        using GCKeeper keeper = new(new RegionStrategy(allowRegions: true), LimboLogs.Instance, runtime);

        // Three leases queue up behind the blocked entry and are all released before the keeper gets to them.
        IDisposable first = keeper.TryStartNoGCRegion();
        IDisposable second = keeper.TryStartNoGCRegion();
        IDisposable third = keeper.TryStartNoGCRegion();
        Assert.That(runtime.EntryRequested.Wait(Patience));
        first.Dispose();
        second.Dispose();
        third.Dispose();
        runtime.RegionEntryGate.Set();

        Assert.That(runtime.Collections.Wait(Patience), "the burst was never swept");
        // The next block's sweep bounds the check that the burst produced no further sweeps.
        RunBlock(keeper, runtime);
        Assert.That(runtime.Collections.Wait(Patience), "the following block was never swept");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(runtime.Collections.Calls, Has.Length.EqualTo(2));
            Assert.That(runtime.RegionsStarted, Is.EqualTo(2), "released requests must not enter regions of their own");
        }
    }

    [Test]
    public void Refused_region_still_defers_the_sweep_to_the_end_of_the_request()
    {
        FakeRuntime runtime = new() { RefuseRegions = true };
        using GCKeeper keeper = new(new RegionStrategy(allowRegions: true), LimboLogs.Instance, runtime);

        IDisposable region = keeper.TryStartNoGCRegion();
        Assert.That(runtime.EntryRequested.Wait(Patience));
        region.Dispose();

        Assert.That(runtime.Collections.Wait(Patience), "the block's garbage was never swept");
        Assert.That(runtime.RegionsEnded, Is.Zero, "nothing to end when the runtime refused the region");
    }

    [Test]
    public void Idle_keeper_sweeps_gen1_without_compaction_or_native_trim_after_a_block()
    {
        FakeRuntime runtime = new();
        using GCKeeper keeper = new(new RegionStrategy(allowRegions: true, sweep: GcLevel.Gen2), LimboLogs.Instance, runtime);

        RunBlock(keeper, runtime);

        Assert.That(runtime.Collections.Wait(Patience), "no sweep followed the block");
        Assert.That(runtime.Collections.Calls, Is.EqualTo(new[] { new Collection(1, GCCollectionMode.Forced, false, false) }));
    }

    [Test]
    public void Payload_reported_before_the_block_ends_suppresses_the_pending_sweep()
    {
        FakeRuntime runtime = new();
        using GCKeeper keeper = new(new RegionStrategy(allowRegions: true), LimboLogs.Instance, runtime);

        // The JSON-RPC layer reports the next payload as soon as its method name is known, before its parameters
        // are bound, which can be while the previous block is still being processed.
        RunBlock(keeper, runtime, whileRunning: ReportNextPayload);

        Assert.That(runtime.Collections.Wait(SettleAllowance), Is.False, "the sweep ran although the next payload was arriving");
        // The next block's sweep shows the keeper is still sweeping when nothing is arriving.
        RunBlock(keeper, runtime);
        Assert.That(runtime.Collections.Wait(Patience), "the second block was never swept");
        Assert.That(runtime.Collections.Calls, Has.Length.EqualTo(1));
    }

    [Test]
    public void Delayed_compacting_collection_follows_an_idle_block()
    {
        FakeRuntime runtime = new();
        using GCKeeper keeper = new(CompactingStrategy(), LimboLogs.Instance, runtime);

        Task before = keeper.PendingCollection;
        RunBlock(keeper, runtime);
        AwaitCollectionDecision(keeper, before);

        Assert.That(runtime.Collections.Wait(Patience, count: 2), "the delayed compacting collection never ran");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(runtime.Collections.Calls, Does.Contain(new Collection(1, GCCollectionMode.Forced, true, true)));
            Assert.That(runtime.Collections.Calls, Does.Contain(new Collection(1, GCCollectionMode.Forced, false, false)));
        }
    }

    [Test]
    public void Payload_reported_before_the_block_ends_cancels_the_compacting_collection()
    {
        FakeRuntime runtime = new();
        using GCKeeper keeper = new(CompactingStrategy(), LimboLogs.Instance, runtime);

        Task before = keeper.PendingCollection;
        RunBlock(keeper, runtime, whileRunning: ReportNextPayload);
        AwaitCollectionDecision(keeper, before);

        Assert.That(runtime.Collections.Calls, Is.Empty, "a collection ran although the next payload was arriving");
        // The next block reschedules nothing (3 s throttle) and only sweeps, which bounds the wait.
        RunBlock(keeper, runtime);
        Assert.That(runtime.Collections.Wait(Patience), "the second block was never swept");
        Assert.That(runtime.Collections.Calls, Is.EqualTo(new[] { new Collection(1, GCCollectionMode.Forced, false, false) }));
    }

    [Test]
    public void Scheduler_is_paused_for_the_lease_and_resumed_on_dispose()
    {
        FakeRuntime runtime = new();
        using GCKeeper keeper = new(new RegionStrategy(allowRegions: true), LimboLogs.Instance, runtime);

        IDisposable region = keeper.TryStartNoGCRegion();
        Assert.That(runtime.RegionStarted.Wait(Patience));
        bool pausedWhileInFlight = GCScheduler.MarkGCPaused();
        region.Dispose();
        bool pausedAfterRelease = GCScheduler.MarkGCPaused();
        GCScheduler.MarkGCResumed();

        Assert.That(runtime.RegionEnded.Wait(Patience));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(pausedWhileInFlight, Is.False, "forced collections must be excluded while a request is in flight");
            Assert.That(pausedAfterRelease, Is.True, "the request's exclusion was not lifted");
        }
    }

    [Test]
    public void Disallowing_strategy_never_touches_the_runtime_and_resumes_the_scheduler_on_dispose()
    {
        FakeRuntime runtime = new();
        using GCKeeper keeper = new(new RegionStrategy(allowRegions: false), LimboLogs.Instance, runtime);

        IDisposable region = keeper.TryStartNoGCRegion();
        bool pausedWhileInFlight = GCScheduler.MarkGCPaused();
        region.Dispose();
        bool pausedAfterRelease = GCScheduler.MarkGCPaused();
        GCScheduler.MarkGCResumed();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(runtime.RegionsStarted, Is.Zero);
            Assert.That(pausedWhileInFlight, Is.False);
            Assert.That(pausedAfterRelease, Is.True);
        }
    }

    [Test]
    public void Disposing_the_keeper_ends_a_region_whose_request_is_still_running()
    {
        FakeRuntime runtime = new();
        GCKeeper keeper = new(new RegionStrategy(allowRegions: true), LimboLogs.Instance, runtime);

        IDisposable region = keeper.TryStartNoGCRegion();
        Assert.That(runtime.RegionStarted.Wait(Patience));
        keeper.Dispose();

        Assert.That(runtime.RegionEnded.Wait(Patience), "shutdown left the process in a no-GC region");
        Assert.DoesNotThrow(region.Dispose, "a lease must still release cleanly after the keeper is gone");
        Assert.That(GCScheduler.MarkGCPaused(), "the lease's exclusion was not lifted");
        GCScheduler.MarkGCResumed();
    }

    [Test]
    public void Runtime_region_is_entered_by_the_keeper_and_left_on_dispose()
    {
        // A refusal is a property of the machine (memory limit, ephemeral budget), not of the keeper.
        try
        {
            if (!System.GC.TryStartNoGCRegion(512.MB + 64.MB, 64.MB, disallowFullBlockingGC: true))
            {
                Assert.Ignore("the runtime refused a no-GC region of this size on this machine");
            }
        }
        catch (ArgumentOutOfRangeException)
        {
            Assert.Ignore("a no-GC region of this size exceeds this machine's ephemeral budget");
        }
        System.GC.EndNoGCRegion();

        using GCKeeper keeper = new(new RegionStrategy(allowRegions: true), LimboLogs.Instance);

        IDisposable region = keeper.TryStartNoGCRegion();
        try
        {
            Assert.That(WaitForLatencyMode(GCLatencyMode.NoGCRegion, expected: true), "the keeper thread never entered the region");
        }
        finally
        {
            // A lease left behind would keep the process in the region for every later test.
            region.Dispose();
        }

        Assert.That(WaitForLatencyMode(GCLatencyMode.NoGCRegion, expected: false), "the region outlived its request");
    }

    private static void RunBlock(GCKeeper keeper, FakeRuntime runtime, Action? whileRunning = null)
    {
        runtime.RegionStarted.Reset();
        runtime.RegionEnded.Reset();
        using (keeper.TryStartNoGCRegion())
        {
            Assert.That(runtime.RegionStarted.Wait(Patience), "the keeper never entered the region");
            whileRunning?.Invoke();
        }
        Assert.That(runtime.RegionEnded.Wait(Patience), "the keeper never left the region");
    }

    /// <summary>What the JSON-RPC layer does when the next payload's method name is known: an arrival with no lease yet.</summary>
    private static void ReportNextPayload() => GCScheduler.EnterLatencySensitiveRequest(carriesBlock: true).Dispose();

    /// <summary>Waits for the collection scheduled after the last block to reach its run-or-stand-down decision.</summary>
    /// <param name="before">The keeper's pending collection from before the block; the new one is installed right after the region ends.</param>
    private static void AwaitCollectionDecision(GCKeeper keeper, Task before)
    {
        long start = Stopwatch.GetTimestamp();
        Task pending = keeper.PendingCollection;
        while (ReferenceEquals(pending, before))
        {
            if (Stopwatch.GetElapsedTime(start) > Patience) Assert.Fail("no collection was scheduled after the block");
            Thread.Sleep(1);
            pending = keeper.PendingCollection;
        }
        Assert.That(pending.Wait(Patience), "the scheduled collection never reached its decision");
    }

    private static bool WaitForLatencyMode(GCLatencyMode mode, bool expected)
    {
        long start = Stopwatch.GetTimestamp();
        while ((GCSettings.LatencyMode == mode) != expected)
        {
            if (Stopwatch.GetElapsedTime(start) > Patience) return false;
            Thread.Sleep(1);
        }
        return true;
    }

    private sealed class RegionStrategy(
        bool allowRegions,
        GcLevel sweep = GcLevel.Gen1,
        GcCompaction compaction = GcCompaction.No,
        int postBlockDelayMs = NeverWithinTestMs) : IGCStrategy
    {
        public int CollectionsPerDecommit => -1;
        public int PostBlockDelayMs => postBlockDelayMs;
        public bool CanStartNoGCRegion() => allowRegions;
        public (GcLevel Generation, GcCompaction Compacting) GetForcedGCParams() => (sweep, compaction);
    }

    private readonly record struct Collection(int Generation, GCCollectionMode Mode, bool Compacting, bool TrimNativeMemory);

    private sealed class CollectionLog
    {
        private readonly ConcurrentQueue<Collection> _calls = new();
        private readonly SemaphoreSlim _arrived = new(0);

        public Collection[] Calls => [.. _calls];

        public void Add(Collection collection)
        {
            _calls.Enqueue(collection);
            _arrived.Release();
        }

        public bool Wait(TimeSpan patience, int count = 1)
        {
            for (int i = 0; i < count; i++)
            {
                if (!_arrived.Wait(patience)) return false;
            }
            return true;
        }
    }

    /// <summary>Stands in for the runtime: region entry can be held back the way an in-flight background GC holds it back.</summary>
    private sealed class FakeRuntime : IGCRuntime
    {
        private volatile bool _inRegion;

        public readonly ManualResetEventSlim RegionEntryGate = new(true);
        public readonly ManualResetEventSlim EntryRequested = new(false);
        public readonly ManualResetEventSlim RegionStarted = new(false);
        public readonly ManualResetEventSlim RegionEnded = new(false);
        public readonly CollectionLog Collections = new();

        public bool RefuseRegions { get; init; }
        public int RegionsStarted { get; private set; }
        public int RegionsEnded { get; private set; }

        public bool InNoGCRegion => _inRegion;

        public bool TryStartNoGCRegion(long totalSize, long lohSize)
        {
            EntryRequested.Set();
            RegionEntryGate.Wait();
            if (RefuseRegions) return false;

            RegionsStarted++;
            _inRegion = true;
            RegionStarted.Set();
            return true;
        }

        public void EndNoGCRegion()
        {
            _inRegion = false;
            RegionsEnded++;
            RegionEnded.Set();
        }

        public void CompactLargeObjectHeapOnce() { }

        public bool Collect(int generation, GCCollectionMode mode, bool compacting, bool trimNativeMemory)
        {
            Collections.Add(new Collection(generation, mode, compacting, trimNativeMemory));
            return true;
        }
    }
}
