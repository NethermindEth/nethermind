// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Synchronization.Peers.AllocationStrategies
{
    public class SatelliteProtocolPeerAllocationStrategy<T>(IPeerAllocationStrategy strategy, string protocol) : FilterPeerAllocationStrategy(strategy) where T : class
    {
        private readonly string _protocol = protocol;

        protected override bool Filter(PeerInfo peerInfo) => peerInfo.SyncPeer.TryGetSatelliteProtocol<T>(_protocol, out _);
    }
}
