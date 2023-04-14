// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Synchronization.Peers.AllocationStrategies
{
    public class SatelliteProtocolPeerAllocationStrategy<T> : FilterPeerAllocationStrategy where T : class
    {
        private readonly string _protocol;

        public SatelliteProtocolPeerAllocationStrategy(IPeerAllocationStrategy strategy, string protocol) : base(strategy)
        {
            _protocol = protocol;
        }

        protected override bool Filter(PeerInfo peerInfo)
        {
            return peerInfo.SyncPeer.TryGetSatelliteProtocol<T>(_protocol, out _);
        }
    }
}
