// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Blockchain;
using Nethermind.Stats;

namespace Nethermind.Synchronization.Peers.AllocationStrategies
{
    public class BySpeedStrategy : IPeerAllocationStrategy
    {
        private readonly TransferSpeedType _speedType;
        private readonly bool _priority;
        private readonly decimal _minDiffPercentageForSpeedSwitch;
        private readonly int _minDiffForSpeedSwitch;
        private readonly Random _random = new();

        // Randomly pick a different peer that is not of the best speed. Encourage updating speed.
        // Does not seems to matter much. But its here if you want to tweak it.
        private readonly double _recalculateSpeedProbability;

        // If the number of peer with known speed is less than this, then always try new peer.
        // The idea is that, if we have at least this amount of peers with known speed, at least one of them should
        // be fast, but we don't want to spend more time trying out other peers.
        private readonly long _desiredPeersWithKnownSpeed;

        public BySpeedStrategy(
            TransferSpeedType speedType,
            bool priority,
            decimal minDiffPercentageForSpeedSwitch = 0.0m,
            int minDiffForSpeedSwitch = 0,
            double recalculateSpeedProbability = 0.025,
            long desiredPeersWithKnownSpeed = 5
        )
        {
            _speedType = speedType;
            _priority = priority;
            _minDiffPercentageForSpeedSwitch = minDiffPercentageForSpeedSwitch;
            _minDiffForSpeedSwitch = minDiffForSpeedSwitch;
            _recalculateSpeedProbability = recalculateSpeedProbability;
            _desiredPeersWithKnownSpeed = desiredPeersWithKnownSpeed;
        }

        public bool CanBeReplaced => false;

        public PeerInfo? Allocate(PeerInfo? currentPeer, IEnumerable<PeerInfo> peers, INodeStatsManager nodeStatsManager, IBlockTree blockTree)
        {
            long nullSpeed = _priority ? -1 : long.MaxValue;
            List<PeerInfo> peersAsList = peers.ToList();

            long peerCount = peersAsList.Count;
            long noSpeedPeerCount = peersAsList.Count(p => nodeStatsManager.GetOrAdd(p.SyncPeer.Node).GetAverageTransferSpeed(_speedType) is null);
            bool shouldRediscoverSpeed = _random.NextDouble() < _recalculateSpeedProbability;
            bool shouldDiscoverSpeed = (peerCount - noSpeedPeerCount) < _desiredPeersWithKnownSpeed;

            long currentSpeed = currentPeer is null ? nullSpeed : nodeStatsManager.GetOrAdd(currentPeer.SyncPeer.Node).GetAverageTransferSpeed(_speedType) ?? nullSpeed;
            (PeerInfo? Info, long TransferSpeed) bestPeer = (currentPeer, currentSpeed);
            bool forceTake = false;

            long peerLeft = peerCount;
            foreach (PeerInfo info in peersAsList)
            {
                (this as IPeerAllocationStrategy).CheckAsyncState(info);

                long? speed = nodeStatsManager.GetOrAdd(info.SyncPeer.Node).GetAverageTransferSpeed(_speedType);
                long averageTransferSpeed = speed ?? 0;

                if (speed is null && shouldDiscoverSpeed)
                {
                    forceTake = true;
                }
                else if (shouldRediscoverSpeed && _random.NextDouble() < (1.0 / peerLeft))
                {
                    forceTake = true;
                }

                if (forceTake ||
                    (averageTransferSpeed == bestPeer.TransferSpeed ?

                        // If its the same speed, just randomly try it. Prevent getting stuck on the same peer on small network.
                        _random.NextSingle() < 0.25 :

                        (_priority ? averageTransferSpeed > bestPeer.TransferSpeed : averageTransferSpeed < bestPeer.TransferSpeed)
                    )
                )
                {
                    bestPeer = (info, averageTransferSpeed);
                }

                if (forceTake) break;
                peerLeft--;
            }

            if (peerCount == 0)
            {
                return currentPeer;
            }

            bool speedRatioExceeded = bestPeer.TransferSpeed / (decimal)Math.Max(1L, currentSpeed) > 1m + _minDiffPercentageForSpeedSwitch;
            bool minSpeedChangeExceeded = bestPeer.TransferSpeed - currentSpeed > _minDiffForSpeedSwitch;
            if (forceTake || (speedRatioExceeded && minSpeedChangeExceeded))
            {
                return bestPeer.Info;
            }

            return currentPeer ?? bestPeer.Info;
        }
    }
}
