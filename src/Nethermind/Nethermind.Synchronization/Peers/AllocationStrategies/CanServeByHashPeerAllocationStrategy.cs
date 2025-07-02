// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Synchronization.StateSync;

namespace Nethermind.Synchronization.Peers.AllocationStrategies;

public class CanServeByHashPeerAllocationStrategy : FilterPeerAllocationStrategy
{
    public CanServeByHashPeerAllocationStrategy(IPeerAllocationStrategy strategy) : base(strategy)
    {
    }

    protected override bool Filter(PeerInfo peerInfo)
    {
        return peerInfo.CanGetNodeData();
    }
}
