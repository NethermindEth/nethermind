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

using System.Collections.Generic;
using System.Linq;
using Nethermind.Blockchain;
using Nethermind.Stats;
using Nethermind.Synchronization.Peers;
using Nethermind.Synchronization.Peers.AllocationStrategies;

namespace Nethermind.Synchronization.FastBlocks
{
    public class FastBlocksAllocationStrategy : IPeerAllocationStrategy
    {
        private readonly long? _minNumber;
        private readonly bool _priority;

        public FastBlocksAllocationStrategy(TransferSpeedType speedType, long? minNumber, bool priority)
        {
            _minNumber = minNumber;
            _priority = priority;

            _slowest = new BySpeedStrategy(speedType, false);
            _fastest = new BySpeedStrategy(speedType, true);
        }

        private IPeerAllocationStrategy _slowest;
        private IPeerAllocationStrategy _fastest;

        public bool CanBeReplaced => false;

        public PeerInfo? Allocate(
            PeerInfo? currentPeer,
            IEnumerable<PeerInfo> peers,
            INodeStatsManager nodeStatsManager,
            IBlockTree blockTree)
        {
            IPeerAllocationStrategy strategy = _priority ? _fastest : _slowest;
            peers = _minNumber == null ? peers : peers.Where(p => p.HeadNumber > _minNumber);
            PeerInfo? allocated = strategy.Allocate(currentPeer, peers, nodeStatsManager, blockTree);
            return allocated;
        }
    }
}
