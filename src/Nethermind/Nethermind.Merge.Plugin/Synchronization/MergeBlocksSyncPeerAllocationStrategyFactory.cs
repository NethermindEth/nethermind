// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Consensus;
using Nethermind.Logging;
using Nethermind.Synchronization.Blocks;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers.AllocationStrategies;

namespace Nethermind.Merge.Plugin.Synchronization;

public class MergeBlocksSyncPeerAllocationStrategyFactory : IPeerAllocationStrategyFactory<BlocksRequest?>
{
    private readonly IPoSSwitcher _poSSwitcher;
    private readonly ILogManager _logManager;
    private readonly IBeaconPivot _beaconPivot;

    public MergeBlocksSyncPeerAllocationStrategyFactory(
        IPoSSwitcher poSSwitcher,
        IBeaconPivot beaconPivot,
        ILogManager logManager)
    {
        _poSSwitcher = poSSwitcher;
        _beaconPivot = beaconPivot;
        _logManager = logManager;
    }

    public IPeerAllocationStrategy Create(BlocksRequest? request)
    {
        // because of the way the generics cannot handle T / T?
        if (request is null)
        {
            throw new ArgumentNullException(
                $"NULL received for allocation in {nameof(MergeBlocksSyncPeerAllocationStrategyFactory)}");
        }

        IPeerAllocationStrategy baseStrategy = new BlocksSyncPeerAllocationStrategy(request.NumberOfLatestBlocksToBeIgnored);
        TotalDiffStrategy preMergeAllocationStrategy = new(baseStrategy);
        PostMergeBlocksSyncPeerAllocationStrategy postMergeStrategy = new(request.NumberOfLatestBlocksToBeIgnored, _beaconPivot);
        MergePeerAllocationStrategy mergeStrategy =
            new(preMergeAllocationStrategy, postMergeStrategy, _poSSwitcher, _logManager);

        return mergeStrategy;
    }
}
