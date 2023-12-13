// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Blockchain;
using Nethermind.Stats;

namespace Nethermind.Synchronization.Peers.AllocationStrategies
{
    /// <summary>
    /// used only for failed allocations
    /// </summary>
    public class NullStrategy : IPeerAllocationStrategy
    {
        private NullStrategy()
        {
        }

        public static IPeerAllocationStrategy Instance { get; } = new NullStrategy();

        public bool CanBeReplaced => false;

        public PeerInfo? Allocate(PeerInfo? currentPeer, IEnumerable<PeerInfo> peers, INodeStatsManager nodeStatsManager, IBlockTree blockTree)
        {
            return null;
        }
    }
}
