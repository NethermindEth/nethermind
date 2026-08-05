// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Network.Contract.P2P;
using Nethermind.Stats.Model;
using NUnit.Framework;

namespace Nethermind.Network.Test;

[Parallelizable(ParallelScope.All)]
public class NHistP2PCapabilityResolverTests
{
    [TestCase(true, true, TestName = "History serving enabled advertises nhist")]
    [TestCase(false, false, TestName = "History serving explicitly disabled advertises nothing")]
    [TestCase(null, false, TestName = "History serving unset advertises nothing")]
    public void Resolve_advertises_nhist_only_when_serving_enabled(bool? historyServingEnabled, bool expected)
    {
        ISyncConfig syncConfig = new SyncConfig { HistoryServingEnabled = historyServingEnabled };
        NHistP2PCapabilityResolver resolver = new(syncConfig);

        HashSet<Capability> capabilities = [];
        resolver.Resolve(capabilities);

        Assert.That(capabilities.Contains(new Capability(Protocol.NHist, NHistVersions.NHist1)), Is.EqualTo(expected));
    }

    [Test]
    public void Resolve_leaves_snap_capability_untouched()
    {
        ISyncConfig syncConfig = new SyncConfig { HistoryServingEnabled = true };
        NHistP2PCapabilityResolver resolver = new(syncConfig);

        HashSet<Capability> capabilities = [];
        resolver.Resolve(capabilities);

        Assert.That(capabilities.Contains(new Capability(Protocol.Snap, SnapVersions.Snap1)), Is.False, "nhist/1 is a distinct capability; this resolver must never add snap/1");
    }
}
