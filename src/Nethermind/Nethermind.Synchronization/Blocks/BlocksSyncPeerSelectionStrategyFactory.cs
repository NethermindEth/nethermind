// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Stats;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers.AllocationStrategies;

namespace Nethermind.Synchronization.Blocks
{
    internal class BlocksSyncPeerAllocationStrategyFactory : StaticPeerAllocationStrategyFactory<BlocksRequest?>
    {
        public static IPeerAllocationStrategy AllocationStrategy = new BySpeedStrategy(TransferSpeedType.Bodies, true);

        public BlocksSyncPeerAllocationStrategyFactory() : base(AllocationStrategy)
        {
        }
    }
}
