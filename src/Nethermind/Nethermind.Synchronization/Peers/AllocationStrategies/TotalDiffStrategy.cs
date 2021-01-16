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
using System.ComponentModel;
using System.Linq;
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
            if (!Enum.IsDefined(typeof(TotalDiffSelectionType), selectionType)) throw new InvalidEnumArgumentException(nameof(selectionType), (int) selectionType, typeof(TotalDiffSelectionType));
            _strategy = strategy ?? throw new ArgumentNullException(nameof(strategy));
            _selectionType = selectionType;
        }

        public bool CanBeReplaced => _strategy.CanBeReplaced;

        public PeerInfo? Allocate(PeerInfo? currentPeer, IEnumerable<PeerInfo> peers, INodeStatsManager nodeStatsManager, IBlockTree blockTree)
        {
            UInt256? currentDiffOrNull = blockTree.BestSuggestedHeader?.TotalDifficulty;
            if (currentDiffOrNull == null)
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
