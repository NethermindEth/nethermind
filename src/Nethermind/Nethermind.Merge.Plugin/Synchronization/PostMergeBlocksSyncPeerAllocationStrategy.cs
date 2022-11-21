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
using Nethermind.Synchronization.Peers;
using Nethermind.Synchronization.Peers.AllocationStrategies;

namespace Nethermind.Merge.Plugin.Synchronization;

public class PostMergeBlocksSyncPeerAllocationStrategy : IPeerAllocationStrategy
{
    private readonly long? _minBlocksAhead;
    private readonly IBeaconPivot _beaconPivot;

    private const decimal MinDiffPercentageForSpeedSwitch = 0.10m;
    private const int MinDiffForSpeedSwitch = 10;
    private readonly BySpeedStrategy _innerStrategy =
        new(TransferSpeedType.Bodies, true, MinDiffPercentageForSpeedSwitch, MinDiffForSpeedSwitch);

    public PostMergeBlocksSyncPeerAllocationStrategy(long? minBlocksAhead, IBeaconPivot beaconPivot)
    {
        _minBlocksAhead = minBlocksAhead;
        _beaconPivot = beaconPivot;
    }

    public bool CanBeReplaced => true;

    public PeerInfo? Allocate(PeerInfo? currentPeer, IEnumerable<PeerInfo> peers, INodeStatsManager nodeStatsManager,
        IBlockTree blockTree)
    {
        IEnumerable<PeerInfo> filteredPeers = peers.Where((info) =>
        {
            if (_beaconPivot.BeaconPivotExists())
            {
                if (info.HeadNumber < _beaconPivot.PivotNumber - 1)
                {
                    // we need to guarantee the peer can have all the block prior to beacon pivot
                    return false;
                }
            }
            else if (info.HeadNumber < (blockTree.BestSuggestedBody?.Number ?? 0) + (_minBlocksAhead ?? 1))
            {
                return false;
            }

            return true;
        });

        return _innerStrategy.Allocate(currentPeer, filteredPeers, nodeStatsManager, blockTree);
    }
}
