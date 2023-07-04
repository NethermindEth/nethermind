// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Synchronization.Peers.AllocationStrategies;

namespace Nethermind.Synchronization.ParallelSync
{
    public class StaticPeerAllocationStrategyFactory<T> : IPeerAllocationStrategyFactory<T>
    {
        private readonly IPeerAllocationStrategy _allocationStrategy;

        public StaticPeerAllocationStrategyFactory(IPeerAllocationStrategy allocationStrategy)
        {
            _allocationStrategy = allocationStrategy;
        }

        public IPeerAllocationStrategy Create(T request) => _allocationStrategy;
    }
}
