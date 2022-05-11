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
using Nethermind.Consensus;
using Nethermind.Logging;
using Nethermind.Network.Config;
using Nethermind.Stats;
using Nethermind.Synchronization;
using Nethermind.Synchronization.Blocks;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers.AllocationStrategies;

namespace Nethermind.Merge.Plugin.Synchronization;

public class MergeBlocksSyncPeerAllocationStrategyFactory : IPeerAllocationStrategyFactory<BlocksRequest?>
{
    private readonly IPoSSwitcher _poSSwitcher;
    private readonly IPivot _pivot;
    private readonly int _maxPeers;
    private readonly ILogManager _logManager;

    public MergeBlocksSyncPeerAllocationStrategyFactory(
        IPoSSwitcher poSSwitcher,
        IPivot pivot,
        int maxPeers,
        ILogManager logManager)
    {
        _poSSwitcher = poSSwitcher;
        _pivot = pivot;
        _maxPeers = maxPeers;
        _logManager = logManager;
    }
    
    public IPeerAllocationStrategy Create(BlocksRequest? request)
    {
        // because of the way the generics cannot handle T / T?
        if (request == null)
        {
            throw new ArgumentNullException(
                $"NULL received for allocation in {nameof(MergeBlocksSyncPeerAllocationStrategyFactory)}");
        }
            
        IPeerAllocationStrategy baseStrategy = new BlocksSyncPeerAllocationStrategy(request.NumberOfLatestBlocksToBeIgnored);
        TotalDiffStrategy preMergeAllocationStrategy = new(baseStrategy);
        PostMergeBlocksSyncPeerAllocationStrategy postMergeStrategy = new(request.NumberOfLatestBlocksToBeIgnored);
        RefreshingPeerAllocationStrategy refreshingStrategy = new(postMergeStrategy, _pivot, _maxPeers, _logManager);
        MergePeerAllocationStrategy mergeStrategy = new(preMergeAllocationStrategy, refreshingStrategy, _poSSwitcher, _logManager);
        
        return mergeStrategy;
    }
}
