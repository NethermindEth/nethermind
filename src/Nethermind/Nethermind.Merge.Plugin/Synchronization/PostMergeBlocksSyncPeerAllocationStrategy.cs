// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
