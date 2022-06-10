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
using Nethermind.Blockchain;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using Nethermind.Synchronization.Peers;
using Nethermind.Synchronization.Peers.AllocationStrategies;

namespace Nethermind.Synchronization.Blocks
{
    public class BlocksSyncPeerAllocationStrategy : IPeerAllocationStrategy
    {
        private readonly long? _minBlocksAhead;
        private readonly ILogger _logger;

        private const decimal MinDiffPercentageForSpeedSwitch = 0.10m;
        private const int MinDiffForSpeedSwitch = 10;

        public BlocksSyncPeerAllocationStrategy(long? minBlocksAhead, ILogManager logManager)
        {
            _minBlocksAhead = minBlocksAhead;
            _logger = logManager.GetClassLogger<BlocksSyncPeerAllocationStrategy>();
        }

        public bool CanBeReplaced => true;

        private long? GetSpeed(INodeStatsManager nodeStatsManager, PeerInfo peerInfo)
        {
            long? headersSpeed = nodeStatsManager.GetOrAdd(peerInfo.SyncPeer.Node).GetAverageTransferSpeed(TransferSpeedType.Headers);
            long? bodiesSpeed = nodeStatsManager.GetOrAdd(peerInfo.SyncPeer.Node).GetAverageTransferSpeed(TransferSpeedType.Bodies);
            if (headersSpeed == null && bodiesSpeed == null)
            {
                return null;
            }
            
            return (headersSpeed ?? 0) + (bodiesSpeed ?? 0);
        }

        public PeerInfo? Allocate(PeerInfo? currentPeer, IEnumerable<PeerInfo> peers, INodeStatsManager nodeStatsManager, IBlockTree blockTree)
        {
            int nullSpeed = -1;
            decimal averageSpeed = 0M;
            int peersCount = 0;
            int consideredPeersCount = 0;

            bool wasNull = currentPeer == null;

            long currentSpeed = wasNull
                ? nullSpeed :
                GetSpeed(nodeStatsManager, currentPeer!) ?? nullSpeed;
            (PeerInfo? Info, long TransferSpeed) fastestPeer = (currentPeer, currentSpeed);
            (PeerInfo? Info, long TransferSpeed) bestDiffPeer = (currentPeer, currentSpeed);

            UInt256 localTotalDiff = blockTree.BestSuggestedHeader?.TotalDifficulty ?? UInt256.Zero;

            foreach (PeerInfo info in peers)
            {
                this.CheckAsyncState(info);
                peersCount++;

                if (_minBlocksAhead != null)
                {
                    if (info.HeadNumber < (blockTree.BestSuggestedHeader?.Number ?? 0) + _minBlocksAhead)
                    {
                        // we need to be able to download some blocks ahead
                        continue;
                    }
                }

                // TODO: this is not needed -> always wrapped
                // if (info.TotalDifficulty <= localTotalDiff)
                // {
                //     // if we require higher difficulty then we need to discard peers with same diff as ours
                //     continue;
                // }

                if (info.TotalDifficulty - localTotalDiff <= 2 && (info.PeerClientType == NodeClientType.Parity || info.PeerClientType == NodeClientType.OpenEthereum))
                {
                    // Parity advertises a better block but never sends it back and then it disconnects after a few conversations like this
                    // Geth responds all fine here
                    // note this is only 2 difficulty difference which means that is just for the POA / Clique chains
                    continue;
                }

                consideredPeersCount++;

                long averageTransferSpeed = GetSpeed(nodeStatsManager, info) ?? 0;

                averageSpeed += averageTransferSpeed;

                if (averageTransferSpeed > fastestPeer.TransferSpeed)
                {
                    fastestPeer = (info, averageTransferSpeed);
                }

                if (info.TotalDifficulty >= (bestDiffPeer.Info?.TotalDifficulty ?? UInt256.Zero))
                {
                    bestDiffPeer = (info, averageTransferSpeed);
                }
            }

            PeerInfo? result;
            if (peersCount == 0)
            {
                result = currentPeer;
            }
            else if (bestDiffPeer.Info == null)
            {
                result = fastestPeer.Info;
            }
            else
            {
                averageSpeed /= peersCount;
                UInt256 difficultyDifference = bestDiffPeer.Info.TotalDifficulty - localTotalDiff;

                // at least 1 diff times 16 blocks of diff
                if (difficultyDifference > 0
                    && difficultyDifference < ((blockTree.Head?.Difficulty ?? 0) + 1) * 16
                    && bestDiffPeer.TransferSpeed > averageSpeed)
                {
                    result = bestDiffPeer.Info;
                }
                else
                {
                    decimal speedRatio = fastestPeer.TransferSpeed / (decimal) Math.Max(1L, currentSpeed);
                    result = speedRatio > 1m + MinDiffPercentageForSpeedSwitch && fastestPeer.TransferSpeed - currentSpeed > MinDiffForSpeedSwitch
                        ? fastestPeer.Info
                        : currentPeer ?? fastestPeer.Info;
                }
            }
            
            if (_logger.IsTrace) _logger.Trace($"{nameof(BlocksSyncPeerAllocationStrategy)}: Result of peer allocation {result} from {peersCount} peers {consideredPeersCount} were considered.");
            return result;
        }

        public override string ToString() => $"{nameof(BlocksSyncPeerAllocationStrategy)} ({_minBlocksAhead})";
    }
}
