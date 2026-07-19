// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime;
using Nethermind.Core;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.GC;
using NUnit.Framework;

namespace Nethermind.Merge.Plugin.Test;

public class GCKeeperTests
{
    [TestCase(0, false)]
    [TestCase(1, true)]
    [TestCase(500, true)]
    [TestCase(GCKeeper.MinPayloadIntervalForDecommitMs - 1, true)]
    [TestCase(GCKeeper.MinPayloadIntervalForDecommitMs, false)]
    [TestCase(12_000, false)]
    public void Decommit_is_deferred_only_while_payloads_stream(long lastPayloadIntervalMs, bool expected) =>
        Assert.That(GCKeeper.ShouldDeferDecommit(lastPayloadIntervalMs), Is.EqualTo(expected));

    [Test]
    public void TryStartNoGCRegion_tracks_interval_between_consecutive_payloads()
    {
        using GCKeeper keeper = new(NoGCStrategy.Instance, LimboLogs.Instance);

        keeper.TryStartNoGCRegion().Dispose();
        Assert.That(keeper.LastPayloadIntervalMs, Is.Zero);

        keeper.TryStartNoGCRegion().Dispose();
        Assert.That(keeper.LastPayloadIntervalMs, Is.GreaterThanOrEqualTo(1));
    }

    [Test]
    [NonParallelizable]
    public void TryStartNoGCRegion_skips_region_when_gc_in_progress()
    {
        using GCKeeper keeper = new(new AllowNoGCRegionStrategy(), LimboLogs.Instance);

        Assert.That(GCScheduler.MarkGCPaused(), Is.True);
        try
        {
            using IDisposable region = keeper.TryStartNoGCRegion();
            Assert.That(GCSettings.LatencyMode, Is.Not.EqualTo(GCLatencyMode.NoGCRegion));
        }
        finally
        {
            GCScheduler.MarkGCResumed();
        }
    }

    private class AllowNoGCRegionStrategy : IGCStrategy
    {
        public int CollectionsPerDecommit => 25;
        public int PostBlockDelayMs => 0;
        public bool CanStartNoGCRegion() => true;
        public (GcLevel Generation, GcCompaction Compacting) GetForcedGCParams() => (GcLevel.Gen1, GcCompaction.No);
    }
}
