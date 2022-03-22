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
using Nethermind.Blockchain;
using Nethermind.Stats;
using Nethermind.Synchronization.Peers;
using Nethermind.Synchronization.Peers.AllocationStrategies;

namespace Nethermind.Merge.Plugin.Synchronization;

public class PostMergeBlocksSyncPeerAllocationStrategy : IPeerAllocationStrategy
{
    private readonly long? _minBlocksAhead;

    private const decimal MinDiffPercentageForSpeedSwitch = 0.10m;
    private const int MinDiffForSpeedSwitch = 10;

    public PostMergeBlocksSyncPeerAllocationStrategy(long? minBlocksAhead)
    {
        _minBlocksAhead = minBlocksAhead;
    }

    public bool CanBeReplaced => true;

    private long? GetSpeed(INodeStatsManager nodeStatsManager, PeerInfo peerInfo)
    {
        long? bodiesSpeed = nodeStatsManager.GetOrAdd(peerInfo.SyncPeer.Node)
            .GetAverageTransferSpeed(TransferSpeedType.Bodies);
        if (bodiesSpeed == null)
        {
            return null;
        }

        return bodiesSpeed ?? 0;
    }

    public PeerInfo? Allocate(PeerInfo? currentPeer, IEnumerable<PeerInfo> peers, INodeStatsManager nodeStatsManager,
        IBlockTree blockTree)
    {
        int nullSpeed = -1;
        int peersCount = 0;

        bool wasNull = currentPeer == null;

        long currentSpeed = wasNull
            ? nullSpeed
            : GetSpeed(nodeStatsManager, currentPeer!) ?? nullSpeed;
        (PeerInfo? Info, long TransferSpeed) fastestPeer = (currentPeer, currentSpeed);

        foreach (PeerInfo info in peers)
        {
            (this as IPeerAllocationStrategy).CheckAsyncState(info);
            peersCount++;
            
            if (info.HeadNumber < (blockTree.BestSuggestedBody?.Number ?? 0) + (_minBlocksAhead ?? 1))
            {
                // we need to be able to download some blocks ahead
                continue;
            }

            long averageTransferSpeed = GetSpeed(nodeStatsManager, info) ?? 0;
            if (averageTransferSpeed > fastestPeer.TransferSpeed)
            {
                fastestPeer = (info, averageTransferSpeed);
            }
        }

        if (peersCount == 0)
        {
            return currentPeer;
        }

        decimal speedRatio = fastestPeer.TransferSpeed / (decimal)Math.Max(1L, currentSpeed);
        if (speedRatio > 1m + MinDiffPercentageForSpeedSwitch
            && fastestPeer.TransferSpeed - currentSpeed > MinDiffForSpeedSwitch)
        {
            return fastestPeer.Info;
        }

        return currentPeer ?? fastestPeer.Info;
    }
}
