//  Copyright (c) 2018 Demerzel Solutions Limited
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

namespace Nethermind.Synchronization.FastBlocks
{
    public class FastBlocksSelectionStrategy : IPeerSelectionStrategy
    {
        private readonly long? _minNumber;
        private readonly bool _priority;

        public FastBlocksSelectionStrategy(long? minNumber, bool priority)
        {
            _minNumber = minNumber;
            _priority = priority;
        }

        public string Name => "fast blocks";
        public bool CanBeReplaced => false;
        public PeerInfo Select(PeerInfo currentPeer, IEnumerable<PeerInfo> peers, INodeStatsManager nodeStatsManager, IBlockTree blockTree)
        {
            IPeerSelectionStrategy strategy = _priority ? BySpeedSelectionStrategy.Fastest : BySpeedSelectionStrategy.Slowest;
            peers = _minNumber == null ? peers : peers.Where(p => p.HeadNumber > _minNumber);
            return strategy.Select(currentPeer, peers, nodeStatsManager, blockTree);
        }
    }
}