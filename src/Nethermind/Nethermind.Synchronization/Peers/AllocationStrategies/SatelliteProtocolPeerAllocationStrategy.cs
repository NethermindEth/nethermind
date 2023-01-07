// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Blockchain;
using Nethermind.Stats;

namespace Nethermind.Synchronization.Peers.AllocationStrategies
{
    public class SatelliteProtocolPeerAllocationStrategy<T> : IPeerAllocationStrategy where T : class
    {
        private readonly IPeerAllocationStrategy _strategy;
        private readonly string _protocol;
        public bool CanBeReplaced => false;

        public SatelliteProtocolPeerAllocationStrategy(IPeerAllocationStrategy strategy, string protocol)
        {
            _strategy = strategy ?? throw new ArgumentNullException(nameof(strategy));
            _protocol = protocol;
        }

        public PeerInfo? Allocate(PeerInfo? currentPeer, IEnumerable<PeerInfo> peers, INodeStatsManager nodeStatsManager, IBlockTree blockTree)
        {
            return _strategy.Allocate(currentPeer, peers.Where(p => p.SyncPeer.TryGetSatelliteProtocol<T>(_protocol, out _)), nodeStatsManager, blockTree);
        }
    }
}
