// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Runtime;
using System.Threading;
using Nethermind.Core;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.GC;
using NUnit.Framework;

namespace Nethermind.Merge.Plugin.Test.GC;

[TestFixture]
[NonParallelizable]
public class GCKeeperTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);

    [Test]
    public void Region_is_entered_by_the_keeper_and_left_on_dispose()
    {
        using GCKeeper keeper = new(new RegionStrategy(allowRegions: true), LimboLogs.Instance);

        long start = Stopwatch.GetTimestamp();
        IDisposable region = keeper.TryStartNoGCRegion();
        TimeSpan requestPathCost = Stopwatch.GetElapsedTime(start);
        try
        {
            Assert.That(requestPathCost, Is.LessThan(TimeSpan.FromMilliseconds(100)), "the request path must not enter the region itself");
            Assert.That(WaitForLatencyMode(GCLatencyMode.NoGCRegion, expected: true), "the keeper thread never entered the region");
        }
        finally
        {
            // A lease left behind would keep the process in the region for every later test.
            region.Dispose();
        }

        Assert.That(WaitForLatencyMode(GCLatencyMode.NoGCRegion, expected: false), "the region outlived its request");
    }

    [Test]
    public void Request_released_before_the_keeper_runs_leaves_no_region_behind()
    {
        using GCKeeper keeper = new(new RegionStrategy(allowRegions: true), LimboLogs.Instance);

        // Dispose immediately: the keeper may still enter the region afterwards, but it must then leave it at once.
        keeper.TryStartNoGCRegion().Dispose();

        Assert.That(WaitForLatencyMode(GCLatencyMode.NoGCRegion, expected: false));
        Thread.Sleep(200);
        Assert.That(GCSettings.LatencyMode, Is.Not.EqualTo(GCLatencyMode.NoGCRegion), "a stale region was left active");
    }

    [Test]
    public void Disallowing_strategy_never_touches_the_runtime()
    {
        using GCKeeper keeper = new(new RegionStrategy(allowRegions: false), LimboLogs.Instance);

        using IDisposable region = keeper.TryStartNoGCRegion();
        Thread.Sleep(100);

        Assert.That(GCSettings.LatencyMode, Is.Not.EqualTo(GCLatencyMode.NoGCRegion));
    }

    [Test]
    public void Scheduler_is_paused_for_the_request_and_resumed_on_dispose()
    {
        using GCKeeper keeper = new(new RegionStrategy(allowRegions: false), LimboLogs.Instance);

        IDisposable region = keeper.TryStartNoGCRegion();
        bool pausedWhileInFlight = GCScheduler.MarkGCPaused();
        region.Dispose();
        bool pausedAfterRelease = GCScheduler.MarkGCPaused();
        GCScheduler.MarkGCResumed();

        Assert.That(pausedWhileInFlight, Is.False, "forced collections must be excluded while a request is in flight");
        Assert.That(pausedAfterRelease, Is.True, "the request's exclusion was not lifted");
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

    private sealed class RegionStrategy(bool allowRegions) : IGCStrategy
    {
        public int CollectionsPerDecommit => -1;
        public int PostBlockDelayMs => 0;
        public bool CanStartNoGCRegion() => allowRegions;
        public (GcLevel Generation, GcCompaction Compacting) GetForcedGCParams() => (GcLevel.NoGC, GcCompaction.No);
    }
}
