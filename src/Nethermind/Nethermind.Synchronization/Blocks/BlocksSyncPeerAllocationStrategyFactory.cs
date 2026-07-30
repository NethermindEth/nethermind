// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Stats;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers;
using Nethermind.Synchronization.Peers.AllocationStrategies;

namespace Nethermind.Synchronization.Blocks
{
    internal class BlocksSyncPeerAllocationStrategyFactory : IPeerAllocationStrategyFactory<BlocksRequest?>
    {
        public static IPeerAllocationStrategy AllocationStrategy { get; } = new BySpeedStrategy(TransferSpeedType.Bodies, true);
        private static IPeerAllocationStrategy BlockAccessListsAllocationStrategy { get; } = new BlockAccessListsPeerAllocationStrategy(AllocationStrategy);

        public IPeerAllocationStrategy Create(BlocksRequest? request) =>
            request?.BlockAccessListsRequests.Count > 0
                ? BlockAccessListsAllocationStrategy
                : AllocationStrategy;

        private sealed class BlockAccessListsPeerAllocationStrategy(IPeerAllocationStrategy strategy) : FilterPeerAllocationStrategy(strategy)
        {
            protected override bool Filter(PeerInfo peerInfo) => peerInfo.SyncPeer.SupportsBlockAccessLists();
        }
    }
}
