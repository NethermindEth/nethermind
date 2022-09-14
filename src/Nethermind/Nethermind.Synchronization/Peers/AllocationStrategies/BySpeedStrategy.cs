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

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Blockchain;
using Nethermind.Stats;
using Prometheus;

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
        private readonly double _recalculateSpeedProbability = 0.025;

        // Randomly pick a peer that has no speed to discover peer with better speed. This number will be multiplied by
        // the proportion of peers without speed, so the effective rate goes down as the number of peer with no speed goes down.
        private readonly double _discoverSpeedProbability = 0.50;

        public BySpeedStrategy(
            TransferSpeedType speedType,
            bool priority,
            decimal minDiffPercentageForSpeedSwitch = 0.0m,
            int minDiffForSpeedSwitch = 0
        )
        {
            _speedType = speedType;
            _priority = priority;
            _minDiffPercentageForSpeedSwitch = minDiffPercentageForSpeedSwitch;
            _minDiffForSpeedSwitch = minDiffForSpeedSwitch;
        }

        public bool CanBeReplaced => false;

        public PeerInfo? Allocate(PeerInfo? currentPeer, IEnumerable<PeerInfo> peers, INodeStatsManager nodeStatsManager, IBlockTree blockTree)
        {
            long nullSpeed = _priority ? -1 : long.MaxValue;
            List<PeerInfo> peersAsList = peers.ToList();

            long peerCount = peersAsList.Count();
            long noPeerCount = peersAsList.Count(p => nodeStatsManager.GetOrAdd(p.SyncPeer.Node).GetAverageTransferSpeed(_speedType) == null);
            double discoverSpeedProbability = _discoverSpeedProbability * noPeerCount / (peerCount == 0 ? 1.0 : (double) peerCount);

            long currentSpeed = currentPeer == null ? nullSpeed : nodeStatsManager.GetOrAdd(currentPeer.SyncPeer.Node).GetAverageTransferSpeed(_speedType) ?? nullSpeed;
            (PeerInfo? Info, long TransferSpeed) bestPeer = (currentPeer, currentSpeed);

            bool recalculateSpeedEitherWay = _random.NextDouble() < _recalculateSpeedProbability;
            bool forceTake = false;

            foreach (PeerInfo info in peersAsList)
            {
                (this as IPeerAllocationStrategy).CheckAsyncState(info);

                long? speed = nodeStatsManager.GetOrAdd(info.SyncPeer.Node).GetAverageTransferSpeed(_speedType);
                long averageTransferSpeed = speed ?? 0;

                if (speed == null && _random.NextDouble() < discoverSpeedProbability)
                {
                    BySpeedStrategyForceDiscovery.WithLabels(_speedType.ToString()).Inc();
                    forceTake = true;
                }
                else if (recalculateSpeedEitherWay && _random.NextDouble() < (1.0 / peerCount))
                {
                    BySpeedStrategyForceRecalculate.WithLabels(_speedType.ToString()).Inc();
                    forceTake = true;
                }

                if (forceTake || (_priority ? averageTransferSpeed > bestPeer.TransferSpeed : averageTransferSpeed < bestPeer.TransferSpeed))
                {
                    bestPeer = (info, averageTransferSpeed);
                }

                if (forceTake) break;
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
