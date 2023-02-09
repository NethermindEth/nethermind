// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using Nethermind.Synchronization.FastSync;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers.AllocationStrategies;

namespace Nethermind.Synchronization.StateSync
{
    public class StateSyncAllocationStrategyFactory : StaticPeerAllocationStrategyFactory<StateSyncBatch>
    {
        private static readonly IPeerAllocationStrategy DefaultStrategy =
            new TotalDiffStrategy(new BySpeedStrategy(TransferSpeedType.NodeData, true),
                TotalDiffStrategy.TotalDiffSelectionType.CanBeSlightlyWorse);

        public StateSyncAllocationStrategyFactory() : base(DefaultStrategy)
        {
        }
    }
}
