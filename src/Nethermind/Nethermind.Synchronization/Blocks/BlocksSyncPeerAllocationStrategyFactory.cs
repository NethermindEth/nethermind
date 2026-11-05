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
        private static readonly IPeerAllocationStrategy BlockAccessListsBySpeed = new BySpeedStrategy(TransferSpeedType.BlockAccessLists, true);
        public static IPeerAllocationStrategy BlockAccessListsAllocationStrategy { get; } =
            new BlockAccessListsPeerAllocationStrategy(BlockAccessListsBySpeed, highestBlockNumber: 0);
        public static IPeerAllocationStrategy ReceiptsAllocationStrategy { get; } = new BySpeedStrategy(TransferSpeedType.Receipts, true);

        public IPeerAllocationStrategy Create(BlocksRequest? request) =>
            request?.BlockAccessListsRequests.Count > 0 ? new BlockAccessListsPeerAllocationStrategy(BlockAccessListsBySpeed, request.BlockAccessListsRequests[^1].Number)
            : request?.ReceiptsRequests.Count > 0 ? ReceiptsAllocationStrategy
            : AllocationStrategy;

        /// <remarks>
        /// Requests are built in ascending block order, so the last one is the highest. A peer whose head is below it
        /// cannot have every list and would answer some unavailable.
        /// </remarks>
        private sealed class BlockAccessListsPeerAllocationStrategy(IPeerAllocationStrategy strategy, ulong highestBlockNumber) : FilterPeerAllocationStrategy(strategy)
        {
            protected override bool Filter(PeerInfo peerInfo) => peerInfo.SyncPeer.SupportsBlockAccessLists() && peerInfo.HeadNumber >= highestBlockNumber;
        }
    }
}
