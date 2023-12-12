// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers.AllocationStrategies;

namespace Nethermind.Synchronization.Blocks
{
    internal class BlocksSyncPeerAllocationStrategyFactory : IPeerAllocationStrategyFactory<BlocksRequest?>
    {
        public IPeerAllocationStrategy Create(BlocksRequest? request)
        {
            // because of the way the generics cannot handle T / T?
            ArgumentNullException.ThrowIfNull(request);

            IPeerAllocationStrategy baseStrategy = new BlocksSyncPeerAllocationStrategy(request.NumberOfLatestBlocksToBeIgnored);

            TotalDiffStrategy totalDiffStrategy = new(baseStrategy);
            return totalDiffStrategy;
        }
    }
}
