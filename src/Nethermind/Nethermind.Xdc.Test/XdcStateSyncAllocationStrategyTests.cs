// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Network.Contract.P2P;
using Nethermind.Stats;
using Nethermind.Synchronization.Peers;
using Nethermind.Synchronization.Peers.AllocationStrategies;
using Nethermind.Xdc.P2P;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Xdc.Test;

[TestFixture, Parallelizable(ParallelScope.All)]
public class XdcStateSyncAllocationStrategyTests
{
    [TestCase(XdcProtocolVersions.Legacy)]
    [TestCase(XdcProtocolVersions.Xdc164)]
    [TestCase(XdcProtocolVersions.Xdc165)]
    public void Every_xdc_version_is_eligible_for_state_sync(byte version) =>
        Assert.That(Filter(version), Is.True);

    [Test]
    public void A_peer_that_serves_neither_node_data_nor_trie_nodes_is_filtered_out() =>
        Assert.That(Filter(EthVersions.Eth68), Is.False);

    private static bool Filter(byte protocolVersion)
    {
        ISyncPeer syncPeer = Substitute.For<ISyncPeer>();
        syncPeer.ProtocolVersion.Returns(protocolVersion);
        PeerInfo peer = new(syncPeer);

        CapturingStrategy capturing = new();
        XdcStateSyncAllocationStrategyFactory.AllocationStrategy strategy = new(capturing);
        strategy.Allocate(null, [peer], Substitute.For<INodeStatsManager>(), Substitute.For<IBlockTree>());

        return capturing.Allowed.Contains(peer);
    }

    private sealed class CapturingStrategy : IPeerAllocationStrategy
    {
        public List<PeerInfo> Allowed { get; } = [];

        public PeerInfo? Allocate(PeerInfo? currentPeer, IEnumerable<PeerInfo> peers, INodeStatsManager nodeStatsManager, IBlockTree blockTree)
        {
            Allowed.AddRange(peers);
            return null;
        }
    }
}
