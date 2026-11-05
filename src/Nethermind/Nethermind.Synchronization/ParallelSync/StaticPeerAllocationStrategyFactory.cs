// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Synchronization.Peers.AllocationStrategies;

namespace Nethermind.Synchronization.ParallelSync
{
    public class StaticPeerAllocationStrategyFactory<T>(IPeerAllocationStrategy allocationStrategy) : IPeerAllocationStrategyFactory<T>
    {
        private readonly IPeerAllocationStrategy _allocationStrategy = allocationStrategy;

        public IPeerAllocationStrategy Create(T request) => _allocationStrategy;
    }
}
