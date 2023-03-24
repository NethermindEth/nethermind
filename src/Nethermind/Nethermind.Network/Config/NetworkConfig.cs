// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Network.Config
{
    public class NetworkConfig : INetworkConfig
    {
        public string? ExternalIp { get; set; }
        public string? LocalIp { get; set; }
        public string? StaticPeers { get; set; }
        public string? DiscoveryDns { get; set; }

        public bool OnlyStaticPeers { get; set; }
        public bool IsPeersPersistenceOn { get; set; } = true;

        [Obsolete]
        public int ActivePeersMaxCount { get => MaxActivePeers; set => MaxActivePeers = value; }
        public int MaxActivePeers { get; set; } = 50;
        public int PriorityPeersMaxCount { get; set; } = 0;
        public int PeersPersistenceInterval { get; set; } = 1000 * 5;
        public int PeersUpdateInterval { get; set; } = 250;
        public int P2PPingInterval { get; set; } = 1000 * 10;
        public int MaxPersistedPeerCount { get; set; } = 2000;
        public int PersistedPeerCountCleanupThreshold { get; set; } = 2200;
        public int MaxCandidatePeerCount { get; set; } = 10000;
        public int CandidatePeerCountCleanupThreshold { get; set; } = 11000;
        public bool DiagTracerEnabled { get; set; } = false;
        public int NettyArenaOrder { get; set; } = INetworkConfig.DefaultNettyArenaOrder;
        public uint MaxNettyArenaCount { get; set; } = INetworkConfig.DefaultMaxNettyArenaCount;
        public string Bootnodes { get; set; } = string.Empty;
        public bool EnableUPnP { get; set; } = false;
        public int DiscoveryPort { get; set; } = 30303;
        public int P2PPort { get; set; } = 30303;
        public long SimulateSendLatencyMs { get; set; } = 0;
        public int ProcessingThreadCount { get; set; } = 0;
    }
}
