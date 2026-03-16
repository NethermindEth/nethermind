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

    [TestCase(EthVersions.Eth67, true, ExpectedResult = true)]
    [TestCase(EthVersions.Eth66, false, ExpectedResult = true)]
    [TestCase(EthVersions.Eth67, false, ExpectedResult = false)]
    public bool Can_allocate_node(int ethVersion, bool hasSnap)
    {
        return IsNodeAllocated(ethVersion, hasSnap);
    }

    private bool IsNodeAllocated(int version, bool hasSnap)
    {
        Node node = new(TestItem.PublicKeyA, new IPEndPoint(0, 0));
        ISyncPeer syncPeer = Substitute.For<ISyncPeer>();
        syncPeer.Node.Returns(node);
        syncPeer.ProtocolVersion.Returns((byte)version);
        syncPeer.TryGetSatelliteProtocol(Protocol.Snap, out Arg.Any<ISnapSyncPeer>()).Returns(
            x =>
            {
                if (hasSnap)
                {
                    x[1] = new object();
                    return true;
                }

                x[1] = null;
                return false;
            });
        PeerInfo peerInfo = new PeerInfo(syncPeer);

        return _strategy.Allocate(null, new List<PeerInfo>() { peerInfo }, Substitute.For<INodeStatsManager>(),
            Substitute.For<IBlockTree>()) == peerInfo;
    }

    private class NoopAllocationStrategy : IPeerAllocationStrategy
    {
        public bool CanBeReplaced => false;
        public PeerInfo? Allocate(PeerInfo? currentPeer, IEnumerable<PeerInfo> peers, INodeStatsManager nodeStatsManager, IBlockTree blockTree)
        {
            return peers.FirstOrDefault();
        }
    }
}
