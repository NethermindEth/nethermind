// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using FastEnumUtility;
using Nethermind.Blockchain;
using Nethermind.Stats;

namespace Nethermind.Synchronization.Peers.AllocationStrategies;

public class LastBlockStrategy : IPeerAllocationStrategy
{
    private readonly IPeerAllocationStrategy _strategy;
    private readonly StrategySelectionType _selectionType;

    public LastBlockStrategy(IPeerAllocationStrategy strategy, StrategySelectionType selectionType = StrategySelectionType.Better)
    {
        if (!FastEnum.IsDefined(selectionType)) throw new InvalidEnumArgumentException(nameof(selectionType), (int)selectionType, typeof(StrategySelectionType));
        _strategy = strategy ?? throw new ArgumentNullException(nameof(strategy));
        _selectionType = selectionType;
    }
    public PeerInfo? Allocate(PeerInfo? currentPeer, IEnumerable<PeerInfo> peers, INodeStatsManager nodeStatsManager, IBlockTree blockTree)
    {
        long? currentBlockOrNull = blockTree.BestSuggestedHeader?.Number;
        if (currentBlockOrNull is null)
            return _strategy.Allocate(currentPeer, peers, nodeStatsManager, blockTree);

        long currentDiff = currentBlockOrNull.Value;
        switch (_selectionType)
        {
            case StrategySelectionType.Better:
                currentDiff += 1;
                break;
            case StrategySelectionType.AtLeastTheSame:
                break;
            case StrategySelectionType.CanBeSlightlyWorse:
                currentDiff -= 1;
                break;
            default:
                throw new ArgumentOutOfRangeException();
        }

        var selectedPeers = peers.Where(p => p.HeadNumber >= currentDiff).ToArray();
        return _strategy.Allocate(currentPeer, selectedPeers, nodeStatsManager, blockTree);
    }
}
