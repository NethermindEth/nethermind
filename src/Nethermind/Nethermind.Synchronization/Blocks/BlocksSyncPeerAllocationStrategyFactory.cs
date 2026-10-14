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

        // Ranked by the speed of the data requested: a peer fast with bodies but slow or stingy with
        // access lists or receipts would otherwise keep winning the requests that hold back processing.
        public static IPeerAllocationStrategy BlockAccessListsAllocationStrategy { get; } =
            new BlockAccessListsPeerAllocationStrategy(new BySpeedStrategy(TransferSpeedType.BlockAccessLists, true));
        public static IPeerAllocationStrategy ReceiptsAllocationStrategy { get; } = new BySpeedStrategy(TransferSpeedType.Receipts, true);

        public IPeerAllocationStrategy Create(BlocksRequest? request) =>
            request?.BlockAccessListsRequests.Count > 0 ? BlockAccessListsAllocationStrategy
            : request?.ReceiptsRequests.Count > 0 ? ReceiptsAllocationStrategy
            : AllocationStrategy;

        private sealed class BlockAccessListsPeerAllocationStrategy(IPeerAllocationStrategy strategy) : FilterPeerAllocationStrategy(strategy)
        {
            protected override bool Filter(PeerInfo peerInfo) => peerInfo.SyncPeer.SupportsBlockAccessLists();
        }
    }
}
