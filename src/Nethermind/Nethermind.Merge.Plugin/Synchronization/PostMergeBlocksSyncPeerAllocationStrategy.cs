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
using Nethermind.Logging;
using Nethermind.Stats;
using Nethermind.Synchronization.Peers;
using Nethermind.Synchronization.Peers.AllocationStrategies;
using Prometheus;
using Metrics = Prometheus.Metrics;

namespace Nethermind.Merge.Plugin.Synchronization;

public class PostMergeBlocksSyncPeerAllocationStrategy : IPeerAllocationStrategy
{
    private readonly long? _minBlocksAhead;
    private readonly IBeaconPivot _beaconPivot;
    private readonly ILogger _logger;

    private const decimal MinDiffPercentageForSpeedSwitch = 0.10m;
    private const int MinDiffForSpeedSwitch = 10;

    private static Gauge PeersWithNoSpeed = Metrics.CreateGauge("nethermind_peers_with_no_speed", "Peers with not speed");

    public PostMergeBlocksSyncPeerAllocationStrategy(long? minBlocksAhead, IBeaconPivot beaconPivot, ILogManager logManager)
    {
        _minBlocksAhead = minBlocksAhead;
        _beaconPivot = beaconPivot;
        _logger = logManager.GetClassLogger<PostMergeBlocksSyncPeerAllocationStrategy>();
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

        int peersWithNoSpeed = 0;
        foreach (PeerInfo info in peers)
        {
            (this as IPeerAllocationStrategy).CheckAsyncState(info);
            peersCount++;

            if (_beaconPivot.BeaconPivotExists())
            {
                if (info.HeadNumber < _beaconPivot.PivotNumber - 1)
                {
                    // we need to guarantee the peer can have all the block prior to beacon pivot
                    continue;
                }
            } else if (info.HeadNumber < (blockTree.BestSuggestedBody?.Number ?? 0) + (_minBlocksAhead ?? 1)) {
               continue;
            }

            long? speed = GetSpeed(nodeStatsManager, info);
            long effectiveSpeed = speed ?? 0;

            if (speed == null)
            {
                peersWithNoSpeed++;
            }

            // If we don't know the speed of this peer, randomly decide if we should try it by setting it's speed
            // to very high so that we know if we have better peer.
            // Note, this roll runs (every second * number of peer with unknown speed). So the percentage of the
            // time it would get in effect PER SECOND is 1-((1-P)^N), Or:
            // 53% when N is 50
            // 26% when N is 20
            // 7 % when N is 5
            // So, quite more often than the number suggest.
            if (speed == null && Random.Shared.NextDouble() < 0.015)
            {
                effectiveSpeed = long.MaxValue;
            }
            _logger.Info($"The speed for {info} is {effectiveSpeed}");

            if (effectiveSpeed > fastestPeer.TransferSpeed)
            {
                fastestPeer = (info, effectiveSpeed);
            }
        }

        _logger.Info($"Peers with no speed {peersWithNoSpeed}");
        PeersWithNoSpeed.Set(peersWithNoSpeed);

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
