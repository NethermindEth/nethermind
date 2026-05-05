// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Blockchain;
using Nethermind.Stats;

namespace Nethermind.Synchronization.Peers.AllocationStrategies
{
    /// <summary>
    /// I believe that this interface should actually make it to the original class (and not stay in test)
    /// Then whenever Borrow is invoked - we can pass the peer selection strategy and it can be very helpful when replacing.
    /// Then it can even have IsUpgradeable field
    /// </summary>
    public interface IPeerAllocationStrategy
    {
        PeerInfo? Allocate(
            PeerInfo? currentPeer,
            IEnumerable<PeerInfo> peers,
            INodeStatsManager nodeStatsManager,
            IBlockTree blockTree);
    }
}
