// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Db;
using Nethermind.Network.Contract.P2P;
using Nethermind.Stats.Model;
using NUnit.Framework;

namespace Nethermind.Network.Test;

[Parallelizable(ParallelScope.All)]
public class NHistP2PCapabilityResolverTests
{
    [TestCase(true, false, 0ul, true, TestName = "History serving enabled advertises nhist")]
    [TestCase(false, false, 0ul, false, TestName = "History serving explicitly disabled and no consumption advertises nothing")]
    [TestCase(null, false, 0ul, false, TestName = "History serving unset and no consumption advertises nothing")]
    [TestCase(null, true, 0ul, true, TestName = "Archive clone client advertises nhist without serving")]
    [TestCase(false, true, 0ul, true, TestName = "Archive clone client advertises nhist even when serving explicitly disabled")]
    [TestCase(null, false, 432000ul, true, TestName = "Windowed backfill client advertises nhist without serving")]
    public void Resolve_advertises_nhist_for_servers_and_consumers(bool? historyServingEnabled, bool cloneEnabled, ulong retentionBlocks, bool expected)
    {
        ISyncConfig syncConfig = new SyncConfig { HistoryServingEnabled = historyServingEnabled };
        IFlatDbConfig flatDbConfig = new FlatDbConfig
        {
            HistoryEnabled = true,
            HistoryArchiveCloneEnabled = cloneEnabled,
            HistoryRetentionBlocks = retentionBlocks
        };
        NHistP2PCapabilityResolver resolver = new(syncConfig, flatDbConfig);

        HashSet<Capability> capabilities = [];
        resolver.Resolve(capabilities);

        Assert.That(capabilities.Contains(new Capability(Protocol.NHist, NHistVersions.NHist1)), Is.EqualTo(expected));
    }

    [Test]
    public void Resolve_leaves_snap_capability_untouched()
    {
        ISyncConfig syncConfig = new SyncConfig { HistoryServingEnabled = true };
        NHistP2PCapabilityResolver resolver = new(syncConfig, new FlatDbConfig());

        HashSet<Capability> capabilities = [];
        resolver.Resolve(capabilities);

        Assert.That(capabilities.Contains(new Capability(Protocol.Snap, SnapVersions.Snap1)), Is.False, "nhist/1 is a distinct capability; this resolver must never add snap/1");
    }
}
