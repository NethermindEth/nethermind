//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
// 

using Nethermind.Blockchain;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Consensus;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.Synchronization;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using Nethermind.Synchronization.Blocks;
using Nethermind.Synchronization.Peers;
using Nethermind.Synchronization.Peers.AllocationStrategies;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Merge.Plugin.Test.Synchronization;

public class MergePeerAllocationStrategyTests
{

    [Test]
    public void Should_allocate_by_totalDifficulty_before_the_merge()
    {
        ulong[] totalDifficulties = {1,3,2};
        int[] averageSpeed = {5,8,10};
        PublicKey[] publicKeys = {TestItem.PublicKeyA, TestItem.PublicKeyB, TestItem.PublicKeyC};
        PeerInfo[] peers = new PeerInfo[3];
        INodeStatsManager _nodeStatsManager = Substitute.For<INodeStatsManager>();
        for (int i = 0; i < 3; i++)
        {
            ISyncPeer syncPeer = Substitute.For<ISyncPeer>();
            syncPeer.IsInitialized.Returns(true);
            Node node = new Node(publicKeys[i], "192.168.1.18", i);
            syncPeer.Node.Returns(node);
            syncPeer.TotalDifficulty.Returns(new UInt256(totalDifficulties[i])); 
            peers[i] = new PeerInfo(syncPeer);
            INodeStats nodeStats = Substitute.For<INodeStats>();
            nodeStats.GetAverageTransferSpeed(Arg.Any<TransferSpeedType>()).Returns(averageSpeed[i]);
            _nodeStatsManager.GetOrAdd(peers[i].SyncPeer.Node).Returns(nodeStats);
        }
        IPoSSwitcher poSSwitcher = Substitute.For<IPoSSwitcher>();
        poSSwitcher.TerminalTotalDifficulty.Returns(new UInt256(5));
        poSSwitcher.HasEverReachedTerminalBlock().Returns(false);
        poSSwitcher.TransitionFinished.Returns(false);
        IPeerAllocationStrategy mergePeerAllocationStrategy =
            (new MergeBlocksSyncPeerAllocationStrategyFactory(poSSwitcher, Substitute.For<ILogManager>())).Create(new BlocksRequest());
        IBlockTree _blockTree = Substitute.For<IBlockTree>();
        PeerInfo? info = mergePeerAllocationStrategy.Allocate(null, peers, _nodeStatsManager, _blockTree);
        
        Assert.AreEqual(info,peers[1]); // peer with highest total difficulty
    }
    
    [Test]
        public void Should_allocate_by_speed_post_merge()
        {
            ulong[] totalDifficulties = {1,3,2};
            int[] averageSpeed = {5,8,10};
            PublicKey[] publicKeys = {TestItem.PublicKeyA, TestItem.PublicKeyB, TestItem.PublicKeyC};
            PeerInfo[] peers = new PeerInfo[3];
            INodeStatsManager _nodeStatsManager = Substitute.For<INodeStatsManager>();
            for (int i = 0; i < 3; i++)
            {
                ISyncPeer syncPeer = Substitute.For<ISyncPeer>();
                syncPeer.IsInitialized.Returns(true);
                Node node = new Node(publicKeys[i], "192.168.1.18", i);
                syncPeer.Node.Returns(node);
                syncPeer.TotalDifficulty.Returns(new UInt256(totalDifficulties[i])); 
                peers[i] = new PeerInfo(syncPeer);
                peers[i].HeadNumber.Returns(1);
                INodeStats nodeStats = Substitute.For<INodeStats>();
                nodeStats.GetAverageTransferSpeed(Arg.Any<TransferSpeedType>()).Returns(averageSpeed[i]);
                _nodeStatsManager.GetOrAdd(peers[i].SyncPeer.Node).Returns(nodeStats);
            }
            IPoSSwitcher poSSwitcher = Substitute.For<IPoSSwitcher>();
            poSSwitcher.TerminalTotalDifficulty.Returns(new UInt256(1));
            poSSwitcher.HasEverReachedTerminalBlock().Returns(true);
            IPeerAllocationStrategy mergePeerAllocationStrategy =
                (new MergeBlocksSyncPeerAllocationStrategyFactory(poSSwitcher, Substitute.For<ILogManager>())).Create(new BlocksRequest());
            IBlockTree _blockTree = Substitute.For<IBlockTree>();
            PeerInfo? info = mergePeerAllocationStrategy.Allocate(null, peers, _nodeStatsManager, _blockTree);
            
            Assert.AreEqual(info,peers[2]); // peer with highest highest speed
        }
    
}
