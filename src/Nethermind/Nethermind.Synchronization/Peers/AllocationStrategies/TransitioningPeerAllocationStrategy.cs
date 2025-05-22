// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Blockchain;
using Nethermind.Consensus;
using Nethermind.Stats;

namespace Nethermind.Synchronization.Peers.AllocationStrategies;

/// <inheritdoc />
/// <summary>
/// Uses <see cref="TotalDiffStrategy"/> before the merge and <see cref="LastBlockStrategy"/> after.
/// </summary>
public class TransitioningPeerAllocationStrategy : IPeerAllocationStrategy
{
    private IPeerAllocationStrategy _currentStrategy;

    public TransitioningPeerAllocationStrategy(IPoSSwitcher poSSwitcher, IPeerAllocationStrategy innerStrategy)
    {
        if (poSSwitcher.TransitionFinished)
        {
            _currentStrategy = CreatePostMergeStrategy(innerStrategy);
        }
        else
        {
            poSSwitcher.Transitioned += (_, _) => _currentStrategy = CreatePostMergeStrategy(innerStrategy);
            _currentStrategy = CreatePreMergeStrategy(innerStrategy);
        }
    }

    private static IPeerAllocationStrategy CreatePreMergeStrategy(IPeerAllocationStrategy innerStrategy) =>
        new TotalDiffStrategy(innerStrategy, StrategySelectionType.CanBeSlightlyWorse);

    private static IPeerAllocationStrategy CreatePostMergeStrategy(IPeerAllocationStrategy innerStrategy) =>
        new LastBlockStrategy(innerStrategy, StrategySelectionType.CanBeSlightlyWorse);

    public PeerInfo? Allocate(PeerInfo? currentPeer, IEnumerable<PeerInfo> peers, INodeStatsManager nodeStatsManager, IBlockTree blockTree) =>
        _currentStrategy.Allocate(currentPeer, peers, nodeStatsManager, blockTree);
}
