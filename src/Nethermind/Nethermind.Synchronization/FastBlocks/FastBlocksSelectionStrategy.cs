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
    public class FastBlocksAllocationStrategy(TransferSpeedType speedType, long? minNumber, bool priority) : IPeerAllocationStrategy
    {
        private readonly long? _minNumber = minNumber;
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
            peers = _minNumber is null ? peers : peers.Where(p => p.HeadNumber >= _minNumber);
            PeerInfo? allocated = strategy.Allocate(currentPeer, peers, nodeStatsManager, blockTree);
            return allocated;
        }
    }
}
