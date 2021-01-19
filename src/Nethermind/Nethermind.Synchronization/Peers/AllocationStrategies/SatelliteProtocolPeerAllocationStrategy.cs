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
// 

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Blockchain;
using Nethermind.Stats;

namespace Nethermind.Synchronization.Peers.AllocationStrategies
{
    public class SatelliteProtocolPeerAllocationStrategy<T> : IPeerAllocationStrategy where T : class
    {
        private readonly IPeerAllocationStrategy _strategy;
        private readonly string _protocol;
        public bool CanBeReplaced => false;

        public SatelliteProtocolPeerAllocationStrategy(IPeerAllocationStrategy strategy, string protocol)
        {
            _strategy = strategy ?? throw new ArgumentNullException(nameof(strategy));
            _protocol = protocol;
        }
        
        public PeerInfo? Allocate(PeerInfo? currentPeer, IEnumerable<PeerInfo> peers, INodeStatsManager nodeStatsManager, IBlockTree blockTree) => 
            _strategy.Allocate(currentPeer, peers.Where(p => p.SyncPeer.TryGetSatelliteProtocol<T>(_protocol, out _)), nodeStatsManager, blockTree);
    }
}
