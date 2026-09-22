// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using Nethermind.Core.Memory;
using Nethermind.Logging;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Core.Test;

[NonParallelizable]
public class ZzScratchLeaseTests
{
    [Test]
    public void Third_overlapping_payload_also_takes_a_lease()
    {
        List<IThreadPoolWorkItem> queued = [];
        Runtime runtime = new();
        IGCStrategy strategy = Substitute.For<IGCStrategy>();
        strategy.CanStartNoGCRegion().Returns(true);
        strategy.GetForcedGCParams().Returns((GcLevel.NoGC, GcCompaction.No));
        using GCKeeper keeper = new(strategy, NullLogManager.Instance, runtime, queued.Add);

        IDisposable first = keeper.TryStartNoGCRegion();
        queued[0].Execute();
        Assert.That(runtime.IsActive, Is.True);

        IDisposable second = keeper.TryStartNoGCRegion();
        IDisposable third = keeper.TryStartNoGCRegion();
        IDisposable fourth = keeper.TryStartNoGCRegion();

        Assert.That(queued, Has.Count.EqualTo(1), "no other region admitted");

        first.Dispose();
        second.Dispose();
        third.Dispose();
        Assert.That(runtime.IsActive, Is.True, "region still held by the fourth payload's lease -> more than two sharers");
        fourth.Dispose();
        Assert.That(runtime.IsActive, Is.False);
        Assert.That(runtime.Ends, Is.EqualTo(1));
    }

    private sealed class Runtime : IGcRegionRuntime
    {
        public List<(GcLevel, GCCollectionMode, GcCompaction)> Collections { get; } = [];
        public bool Collect(GcLevel g, GCCollectionMode m, GcCompaction c) { Collections.Add((g, m, c)); return true; }
        public int Starts { get; private set; }
        public int Ends { get; private set; }
        public bool IsActive { get; private set; }
        public bool TryStart(long totalSize, long lohSize) { Starts++; return IsActive = true; }
        public void End() { Ends++; IsActive = false; }
    }
}
