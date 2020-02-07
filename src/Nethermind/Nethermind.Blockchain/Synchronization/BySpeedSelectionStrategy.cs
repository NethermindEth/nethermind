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
using Nethermind.Stats;

namespace Nethermind.Blockchain.Synchronization
{
    public class BySpeedSelectionStrategy : IPeerSelectionStrategy
    {
        private readonly bool _priority;

        private BySpeedSelectionStrategy(bool priority)
        {
            _priority = priority;
        }

        public static BySpeedSelectionStrategy Slowest = new BySpeedSelectionStrategy(false);
        public static BySpeedSelectionStrategy Fastest = new BySpeedSelectionStrategy(true);

        public string Name => "fast blocks";
        public bool CanBeReplaced => false;

        public PeerInfo Select(PeerInfo currentPeer, IEnumerable<PeerInfo> peers, INodeStatsManager nodeStatsManager, IBlockTree blockTree)
        {
            long currentSpeed = currentPeer == null ? 0 : nodeStatsManager.GetOrAdd(currentPeer.SyncPeer.Node).GetAverageTransferSpeed() ?? 0;
            (PeerInfo Info, long TransferSpeed) bestPeer = (currentPeer, currentSpeed);

            foreach (PeerInfo info in peers)
            {
                (this as IPeerSelectionStrategy).CheckAsyncState(info);

                long averageTransferSpeed = nodeStatsManager.GetOrAdd(info.SyncPeer.Node).GetAverageTransferSpeed() ?? 0;
                if (_priority ? averageTransferSpeed > bestPeer.TransferSpeed : averageTransferSpeed <= bestPeer.TransferSpeed)
                {
                    bestPeer = (info, averageTransferSpeed);
                }
            }

            return bestPeer.Info;
        }
    }
}