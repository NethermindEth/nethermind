// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using System.Net;
using FluentAssertions;
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
    private static IPeerAllocationStrategy _strategy = new StateSyncAllocationStrategyFactory.AllocationStrategy(new NoopAllocationStrategy());

    [Test]
    public void Can_allocate_node_with_snap()
    {
        IsNodeAllocated(EthVersions.Eth67, true).Should().BeTrue();
    }

    [Test]
    public void Can_allocate_pre_eth67_node()
    {
        IsNodeAllocated(EthVersions.Eth66, false).Should().BeTrue();
    }

    [Test]
    public void Cannot_allocated_eth67_with_no_snap()
    {
        IsNodeAllocated(EthVersions.Eth67, false).Should().BeFalse();
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
