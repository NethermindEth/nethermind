// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Blockchain;
using Nethermind.Stats;

namespace Nethermind.Synchronization.Peers.AllocationStrategies
{
    public class StaticStrategy : IPeerAllocationStrategy
    {
        private readonly PeerInfo _peerInfo;

        public StaticStrategy(PeerInfo peerInfo)
        {
            _peerInfo = peerInfo;
        }

        public bool CanBeReplaced => false;
        public PeerInfo Allocate(PeerInfo? currentPeer, IEnumerable<PeerInfo> peers, INodeStatsManager nodeStatsManager, IBlockTree blockTree)
        {
            return _peerInfo;
        }
    }
}
