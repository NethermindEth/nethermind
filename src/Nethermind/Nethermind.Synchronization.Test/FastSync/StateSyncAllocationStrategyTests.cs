// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using System.Net;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core.Test.Builders;
using Nethermind.Network.Contract.P2P;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using Nethermind.Synchronization.Peers;
using Nethermind.Synchronization.Peers.AllocationStrategies;
using Nethermind.Synchronization.StateSync;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Synchronization.Test.FastSync;

public class StateSyncAllocationStrategyTests
{
    private static readonly IPeerAllocationStrategy _strategy = new StateSyncAllocationStrategyFactory.AllocationStrategy(new NoopAllocationStrategy());

    [TestCase(EthVersions.Eth67, SnapVersions.Snap1, ExpectedResult = true)]
    [TestCase(EthVersions.Eth66, null, ExpectedResult = true)]
    [TestCase(EthVersions.Eth67, null, ExpectedResult = false)]
    [TestCase(EthVersions.Eth67, SnapVersions.Snap2, ExpectedResult = false)]
    [TestCase(EthVersions.Eth66, SnapVersions.Snap2, ExpectedResult = true)]
    public bool Can_allocate_node(byte ethVersion, byte? snapVersion) =>
        IsNodeAllocated(ethVersion, snapVersion);

    private bool IsNodeAllocated(byte version, byte? snapVersion)
    {
        Node node = new(TestItem.PublicKeyA, new IPEndPoint(0, 0));
        ISyncPeer syncPeer = Substitute.For<ISyncPeer>();
        syncPeer.Node.Returns(node);
        syncPeer.ProtocolVersion.Returns(version);
        syncPeer.TryGetSatelliteProtocol(Protocol.Snap, out Arg.Any<ISnapSyncPeer>()).Returns(
            x =>
            {
                if (snapVersion is null)
                {
                    x[1] = null;
                    return false;
                }

                ISnapSyncPeer snapPeer = Substitute.For<ISnapSyncPeer>();
                snapPeer.SnapProtocolVersion.Returns(snapVersion.Value);
                x[1] = snapPeer;
                return true;
            });
        PeerInfo peerInfo = new(syncPeer);

        return _strategy.Allocate(null, new List<PeerInfo>() { peerInfo }, Substitute.For<INodeStatsManager>(),
            Substitute.For<IBlockTree>()) == peerInfo;
    }

    private class NoopAllocationStrategy : IPeerAllocationStrategy
    {
        public bool CanBeReplaced => false;
        public PeerInfo? Allocate(PeerInfo? currentPeer, IEnumerable<PeerInfo> peers, INodeStatsManager nodeStatsManager, IBlockTree blockTree) =>
            peers.FirstOrDefault();
    }
}
