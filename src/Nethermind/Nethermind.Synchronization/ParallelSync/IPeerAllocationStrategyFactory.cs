// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Synchronization.Peers.AllocationStrategies;

namespace Nethermind.Synchronization.ParallelSync
{
    public interface IPeerAllocationStrategyFactory<in T>
    {
        IPeerAllocationStrategy Create(T request);
    }
}
