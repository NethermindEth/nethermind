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

using System.Collections.Generic;
using System.Linq;
using Nethermind.Blockchain;
using Nethermind.Stats;
using Nethermind.Stats.Model;

namespace Nethermind.Synchronization.Peers.AllocationStrategies;

public class ClientTypeStrategy : IPeerAllocationStrategy
{
    private readonly IPeerAllocationStrategy _strategy;
    private readonly bool _allowOtherIfNone;
    private readonly HashSet<NodeClientType> _supportedClientTypes;

    public ClientTypeStrategy(IPeerAllocationStrategy strategy, bool allowOtherIfNone, params NodeClientType[] supportedClientTypes)
        : this(strategy, allowOtherIfNone, (IEnumerable<NodeClientType>)supportedClientTypes)
    {
    }

    public ClientTypeStrategy(IPeerAllocationStrategy strategy, bool allowOtherIfNone, IEnumerable<NodeClientType> supportedClientTypes)
    {
        _strategy = strategy;
        _allowOtherIfNone = allowOtherIfNone;
        _supportedClientTypes = new HashSet<NodeClientType>(supportedClientTypes);   
    }
    
    public bool CanBeReplaced => _strategy.CanBeReplaced;

    public PeerInfo? Allocate(PeerInfo? currentPeer, IEnumerable<PeerInfo> peers, INodeStatsManager nodeStatsManager, IBlockTree blockTree)
    {
        IEnumerable<PeerInfo> originalPeers = peers;
        peers = peers.Where(p => _supportedClientTypes.Contains(p.PeerClientType));
        
        if (_allowOtherIfNone)
        {
            if (!peers.Any())
            {
                peers = originalPeers;
            }
        }
        return _strategy.Allocate(currentPeer, peers, nodeStatsManager, blockTree);
    }
}
