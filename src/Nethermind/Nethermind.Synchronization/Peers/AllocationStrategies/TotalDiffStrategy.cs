// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using FastEnumUtility;
using Nethermind.Blockchain;
using Nethermind.Int256;
using Nethermind.Stats;

namespace Nethermind.Synchronization.Peers.AllocationStrategies
{
    public class TotalDiffStrategy : IPeerAllocationStrategy
    {
        public enum TotalDiffSelectionType
        {
            Better = 1,
            AtLeastTheSame = 0,
            CanBeSlightlyWorse = -1
        }

        private readonly IPeerAllocationStrategy _strategy;
        private readonly TotalDiffSelectionType _selectionType;

        public TotalDiffStrategy(IPeerAllocationStrategy strategy, TotalDiffSelectionType selectionType = TotalDiffSelectionType.Better)
        {
            if (!FastEnum.IsDefined(selectionType)) throw new InvalidEnumArgumentException(nameof(selectionType), (int)selectionType, typeof(TotalDiffSelectionType));
            _strategy = strategy ?? throw new ArgumentNullException(nameof(strategy));
            _selectionType = selectionType;
        }

        public bool CanBeReplaced => _strategy.CanBeReplaced;

        public PeerInfo? Allocate(PeerInfo? currentPeer, IEnumerable<PeerInfo> peers, INodeStatsManager nodeStatsManager, IBlockTree blockTree)
        {
            UInt256? currentDiffOrNull = blockTree.BestSuggestedHeader?.TotalDifficulty;
            if (currentDiffOrNull is null)
            {
                return _strategy.Allocate(currentPeer, peers, nodeStatsManager, blockTree);
            }

            UInt256 currentDiff = currentDiffOrNull.Value;
            switch (_selectionType)
            {
                case TotalDiffSelectionType.Better:
                    currentDiff += UInt256.One;
                    break;
                case TotalDiffSelectionType.AtLeastTheSame:
                    break;
                case TotalDiffSelectionType.CanBeSlightlyWorse:
                    UInt256 lastBlockDiff = blockTree.BestSuggestedHeader?.Difficulty ?? 0;
                    if (currentDiff >= lastBlockDiff)
                    {
                        currentDiff -= lastBlockDiff;
                    }

                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }

            return _strategy.Allocate(currentPeer, peers.Where(p => p.TotalDifficulty >= currentDiff), nodeStatsManager, blockTree);
        }
    }
}
