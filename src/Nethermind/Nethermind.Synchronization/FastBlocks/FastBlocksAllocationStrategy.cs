// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using Nethermind.Blockchain;
using Nethermind.Stats;
using Nethermind.Synchronization.Peers;
using Nethermind.Synchronization.Peers.AllocationStrategies;

namespace Nethermind.Synchronization.FastBlocks
{
    public class FastBlocksAllocationStrategy(TransferSpeedType speedType, ulong? minNumber, bool priority) : IPeerAllocationStrategy
    {
        internal FastBlocksAllocationStrategy(TransferSpeedType speedType, ulong? minNumber, bool priority, IPeerAllocationStrategy slowest)
            : this(speedType, minNumber, priority) => _slowest = slowest;

        private readonly ulong? _minNumber = minNumber;
        private readonly bool _priority = priority;
        private readonly IPeerAllocationStrategy _slowest = new BySpeedStrategy(speedType, false);
        private readonly IPeerAllocationStrategy _fastest = new BySpeedStrategy(speedType, true);

        public PeerInfo? Allocate(
            PeerInfo? currentPeer,
            IEnumerable<PeerInfo> peers,
            INodeStatsManager nodeStatsManager,
            IBlockTree blockTree)
        {
            IPeerAllocationStrategy strategy = _priority ? _fastest : _slowest;
            bool CanServe(PeerInfo peer) => _minNumber is null || (peer.HeadNumber >= _minNumber &&
                (speedType is not (TransferSpeedType.Bodies or TransferSpeedType.Receipts)
                    || peer.SyncPeer.EarliestBlockNumber <= _minNumber
                    && peer.HistoryFloor(speedType == TransferSpeedType.Bodies ? AllocationContexts.Bodies : AllocationContexts.Receipts) <= _minNumber));
            peers = peers.Where(CanServe);
            if (currentPeer is not null && !CanServe(currentPeer)) currentPeer = null;
            PeerInfo? allocated = strategy.Allocate(currentPeer, peers, nodeStatsManager, blockTree);
            return allocated;
        }
    }
}
