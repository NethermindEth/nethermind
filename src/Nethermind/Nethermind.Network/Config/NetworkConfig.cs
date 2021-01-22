//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

namespace Nethermind.Network.Config
{
    public class NetworkConfig : INetworkConfig
    {
        public string ExternalIp { get; set; } = null;
        public string LocalIp { get; set; }
        public string StaticPeers { get; set; }
        public bool OnlyStaticPeers { get; set; }
        public string TrustedPeers { get; set; } = null;
        public bool IsPeersPersistenceOn { get; set; } = true;
        public int ActivePeersMaxCount { get; set; } = 50;
        public int PeersPersistenceInterval { get; set; } = 1000 * 5;
        public int PeersUpdateInterval { get; set; } = 250;
        public int P2PPingInterval { get; set; } = 1000 * 10;
        public int MaxPersistedPeerCount { get; set; } = 2000;
        public int PersistedPeerCountCleanupThreshold { get; set; } = 2200;
        public int MaxCandidatePeerCount { get; set; } = 10000;
        public int CandidatePeerCountCleanupThreshold { get; set; } = 11000;
        public bool DiagTracerEnabled { get; set; } = false;
        public int NettyArenaOrder { get; set; } = INetworkConfig.DefaultNettyArenaOrder;
        public int DiscoveryPort { get; set; } = 30303;
        public int P2PPort { get; set; } = 30303;
    }
}
