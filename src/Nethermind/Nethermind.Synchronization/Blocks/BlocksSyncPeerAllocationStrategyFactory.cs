// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Collections;
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

        public IPeerAllocationStrategy Create(BlocksRequest? request)
        {
            if (request is null) return AllocationStrategy;

            IPeerAllocationStrategy strategy = request.BlockAccessListsRequests.Count > 0
                ? BlockAccessListsAllocationStrategy
                : AllocationStrategy;

            ulong? lowest = LowestNumber(request.BodiesRequests, LowestNumber(request.BlockAccessListsRequests, LowestNumber(request.ReceiptsRequests, null)));
            return lowest is null ? strategy : new EarliestBlockPeerAllocationStrategy(strategy, lowest.Value);
        }

        /// <summary>
        /// The strategy used to pick peers for blocks from <paramref name="lowestBlockNumber"/> upwards.
        /// </summary>
        public static IPeerAllocationStrategy ForBlocksFrom(ulong lowestBlockNumber) =>
            new EarliestBlockPeerAllocationStrategy(AllocationStrategy, lowestBlockNumber);

        private static ulong? LowestNumber(IOwnedReadOnlyList<BlockHeader>? headers, ulong? current)
        {
            if (headers is null || headers.Count == 0) return current;

            // Requests are assembled in ascending order, so the first header is the lowest.
            ulong number = headers[0].Number;
            return current is null || number < current ? number : current;
        }

        private sealed class BlockAccessListsPeerAllocationStrategy(IPeerAllocationStrategy strategy) : FilterPeerAllocationStrategy(strategy)
        {
            protected override bool Filter(PeerInfo peerInfo) => peerInfo.SyncPeer.SupportsBlockAccessLists();
        }
    }
}
