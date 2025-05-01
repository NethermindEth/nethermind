// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Blockchain;
using Nethermind.Stats;

namespace Nethermind.Synchronization.Peers.AllocationStrategies
{
    public abstract class FilterPeerAllocationStrategy : IPeerAllocationStrategy
    {
        private readonly IPeerAllocationStrategy _nextStrategy;

        public FilterPeerAllocationStrategy(IPeerAllocationStrategy strategy)
        {
            _nextStrategy = strategy ?? throw new ArgumentNullException(nameof(strategy));
        }

        public PeerInfo? Allocate(PeerInfo? currentPeer, IEnumerable<PeerInfo> peers, INodeStatsManager nodeStatsManager, IBlockTree blockTree)
        {
            return _nextStrategy.Allocate(currentPeer, peers.Where(Filter), nodeStatsManager, blockTree);
        }

        protected abstract bool Filter(PeerInfo peerInfo);
    }
}
