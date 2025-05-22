// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus;
using Nethermind.Stats;
using Nethermind.Synchronization.FastSync;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers;
using Nethermind.Synchronization.Peers.AllocationStrategies;

namespace Nethermind.Synchronization.StateSync
{
    public class StateSyncAllocationStrategyFactory(IPoSSwitcher poSSwitcher) : StaticPeerAllocationStrategyFactory<StateSyncBatch>(
        new AllocationStrategy(
            new TransitioningPeerAllocationStrategy(poSSwitcher,
                new BySpeedStrategy(TransferSpeedType.NodeData, true)
            )
        )
    )
    {
        internal class AllocationStrategy : FilterPeerAllocationStrategy
        {
            public AllocationStrategy(IPeerAllocationStrategy strategy) : base(strategy) { }

            protected override bool Filter(PeerInfo peerInfo)
            {
                return peerInfo.CanGetSnapData() || peerInfo.CanGetNodeData();
            }
        }
    }
}
