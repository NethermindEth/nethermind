// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Blockchain;
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

        private sealed class BlockAccessListsPeerAllocationStrategy(IPeerAllocationStrategy strategy) : IPeerAllocationStrategy
        {
            public PeerInfo? Allocate(
                PeerInfo? currentPeer,
                IEnumerable<PeerInfo> peers,
                INodeStatsManager nodeStatsManager,
                IBlockTree blockTree) =>
                strategy.Allocate(IsEligible(currentPeer) ? currentPeer : null, EligiblePeers(peers), nodeStatsManager, blockTree);

            private static IEnumerable<PeerInfo> EligiblePeers(IEnumerable<PeerInfo> peers)
            {
                foreach (PeerInfo peerInfo in peers)
                {
                    if (IsEligible(peerInfo))
                    {
                        yield return peerInfo;
                    }
                }
            }

            private static bool IsEligible(PeerInfo? peerInfo) =>
                peerInfo is not null && peerInfo.SyncPeer.SupportsBlockAccessLists();
        }
    }
}
