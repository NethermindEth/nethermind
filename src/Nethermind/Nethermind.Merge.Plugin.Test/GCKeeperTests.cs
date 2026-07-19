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
    private const long Now = 1_784_500_000;

    [TestCase(Now, 0ul, false)]
    [TestCase(Now, (ulong)(Now - 5), false)]
    [TestCase(Now, (ulong)(Now - GCKeeper.MaxPayloadLagSecondsForDecommit), false)]
    [TestCase(Now, (ulong)(Now - GCKeeper.MaxPayloadLagSecondsForDecommit - 1), true)]
    [TestCase(Now, (ulong)(Now - 3600), true)]
    [TestCase(Now, (ulong)(Now + 5), false)]
    public void Decommit_is_deferred_only_while_catching_up(long nowUnixSeconds, ulong lastPayloadTimestamp, bool expected) =>
        Assert.That(GCKeeper.ShouldDeferDecommit(nowUnixSeconds, lastPayloadTimestamp), Is.EqualTo(expected));

    [Test]
    public void TryStartNoGCRegion_records_last_payload_timestamp()
    {
        using GCKeeper keeper = new(NoGCStrategy.Instance, LimboLogs.Instance);

        keeper.TryStartNoGCRegion(1234).Dispose();
        Assert.That(keeper.LastPayloadTimestamp, Is.EqualTo(1234ul));

        keeper.TryStartNoGCRegion().Dispose();
        Assert.That(keeper.LastPayloadTimestamp, Is.EqualTo(1234ul), "timestamp-less calls should not reset tracking");
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
