// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Blockchain;
using Nethermind.Int256;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using Nethermind.Synchronization.Peers;
using Nethermind.Synchronization.Peers.AllocationStrategies;

namespace Nethermind.Synchronization.Blocks
{
    public class ByTotalDifficultyPeerAllocationStrategy : IPeerAllocationStrategy
    {
        private readonly long? _minBlocksAhead;

        private const decimal MinDiffPercentageForSpeedSwitch = 0.10m;
        private const int MinDiffForSpeedSwitch = 10;

        public ByTotalDifficultyPeerAllocationStrategy(long? minBlocksAhead)
        {
            _minBlocksAhead = minBlocksAhead;
        }

        private static long? GetSpeed(INodeStatsManager nodeStatsManager, PeerInfo peerInfo)
        {
            long? headersSpeed = nodeStatsManager.GetOrAdd(peerInfo.SyncPeer.Node).GetAverageTransferSpeed(TransferSpeedType.Headers);
            long? bodiesSpeed = nodeStatsManager.GetOrAdd(peerInfo.SyncPeer.Node).GetAverageTransferSpeed(TransferSpeedType.Bodies);
            if (headersSpeed is null && bodiesSpeed is null)
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

            bool wasNull = currentPeer is null;

            long currentSpeed = wasNull
                ? nullSpeed :
                GetSpeed(nodeStatsManager, currentPeer!) ?? nullSpeed;
            (PeerInfo? Info, long TransferSpeed) fastestPeer = (currentPeer, currentSpeed);
            (PeerInfo? Info, long TransferSpeed) bestDiffPeer = (currentPeer, currentSpeed);

            UInt256 localTotalDiff = blockTree.BestSuggestedHeader?.TotalDifficulty ?? UInt256.Zero;

            foreach (PeerInfo info in peers)
            {
                info.EnsureInitialized();
                peersCount++;

                if (_minBlocksAhead is not null)
                {
                    if (info.HeadNumber < (blockTree.BestSuggestedHeader?.Number ?? 0) + _minBlocksAhead)
                    {
                        // we need to be able to download some blocks ahead
                        continue;
                    }
                }

                UInt256? remoteTotalDiff = info.TotalDifficulty;
                if (remoteTotalDiff.HasValue &&
                    remoteTotalDiff.Value >= localTotalDiff &&
                    remoteTotalDiff.Value - localTotalDiff <= 2 &&
                    info.PeerClientType is NodeClientType.Parity or NodeClientType.OpenEthereum)
                {
                    // Parity advertises a better block but never sends it back and then it disconnects after a few conversations like this
                    // Geth responds all fine here
                    // note this is only 2 difficulty difference which means that is just for the POA / Clique chains
                    continue;
                }

                long averageTransferSpeed = GetSpeed(nodeStatsManager, info) ?? 0;

                averageSpeed += averageTransferSpeed;

                if (averageTransferSpeed > fastestPeer.TransferSpeed)
                {
                    fastestPeer = (info, averageTransferSpeed);
                }

                if (info.HasEqualOrBetterTDOrBlock(bestDiffPeer.Info))
                {
                    bestDiffPeer = (info, averageTransferSpeed);
                }
            }

            if (peersCount == 0)
            {
                return currentPeer;
            }

            if (bestDiffPeer.Info is null)
            {
                return fastestPeer.Info;
            }

            const int minBlocksDiff = 16;

            averageSpeed /= peersCount;
            if (bestDiffPeer.Info.TotalDifficulty is { } bestPeerTD) // Try to compare by TD if present
            {
                averageSpeed /= peersCount;
                UInt256 difficultyDifference = bestPeerTD > localTotalDiff
                    ? bestPeerTD - localTotalDiff
                    : UInt256.Zero;

                // at least 1 diff times 16 blocks of diff
                if (difficultyDifference > 0
                    && (difficultyDifference >= ((blockTree.Head?.Difficulty ?? 0) + 1) * minBlocksDiff
                        || bestDiffPeer.TransferSpeed > averageSpeed))
                {
                    return bestDiffPeer.Info;
                }
            }
            else // by last block otherwise
            {
                var bestPeerNumber = bestDiffPeer.Info.HeadNumber;
                var localNumber = blockTree.Head?.Number ?? 0;
                var blockDifference = bestPeerNumber > localNumber
                    ? bestPeerNumber - localNumber
                    : 0;

                // at least 16 blocks
                if (blockDifference > 0
                    && (blockDifference >= localNumber + minBlocksDiff
                        || bestDiffPeer.TransferSpeed > averageSpeed))
                {
                    return bestDiffPeer.Info;
                }
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
}
