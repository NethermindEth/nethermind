// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;

namespace Nethermind.Network.Config
{
    public interface INetworkConfig : IConfig
    {
        public const int DefaultNettyArenaOrder = 11;

        [ConfigItem(Description = "Use only if your node cannot resolve external IP automatically.", DefaultValue = "null")]
        string? ExternalIp { get; set; }

        [ConfigItem(Description = "Use only if your node cannot resolve local IP automatically.", DefaultValue = "null")]
        string? LocalIp { get; set; }

        [ConfigItem(Description = "List of nodes for which we will keep the connection on. Static nodes are not counted to the max number of nodes limit.", DefaultValue = "null")]
        string? StaticPeers { get; set; }

        [ConfigItem(Description = "Use tree is available through a DNS name. Keep it empty for the default of {chainName}.ethdisco.net", DefaultValue = "null")]
        string? DiscoveryDns { get; set; }

        [ConfigItem(Description = "If set to 'true' then no connections will be made to non-static peers.", DefaultValue = "false")]
        bool OnlyStaticPeers { get; set; }

        [ConfigItem(DisabledForCli = true, HiddenFromDocs = true, Description = "If 'false' then discovered node list will be cleared on each restart.", DefaultValue = "true")]
        bool IsPeersPersistenceOn { get; set; }

        [ConfigItem(Description = "[OBSOLETE](Use MaxActivePeers instead) Max number of connected peers.", DefaultValue = "50")]
        int ActivePeersMaxCount { get; set; }

        [ConfigItem(Description = "Max number of priority peers. Can be overwritten by value from plugin config.", DefaultValue = "0")]
        int PriorityPeersMaxCount { get; set; }

        [ConfigItem(Description = "Same as ActivePeersMaxCount.", DefaultValue = "50")]
        int MaxActivePeers { get; set; }

        [ConfigItem(DisabledForCli = true, HiddenFromDocs = true, DefaultValue = "5000")]
        int PeersPersistenceInterval { get; set; }

        [ConfigItem(DisabledForCli = true, HiddenFromDocs = true, DefaultValue = "250")]
        int PeersUpdateInterval { get; set; }

        [ConfigItem(DisabledForCli = true, HiddenFromDocs = true, DefaultValue = "10000")]
        int P2PPingInterval { get; }

        [ConfigItem(Description = "UDP port number for incoming discovery connections. Keep same as TCP/IP port because using different values has never been tested.", DefaultValue = "30303")]
        int DiscoveryPort { get; set; }

        [ConfigItem(Description = "TPC/IP port number for incoming P2P connections.", DefaultValue = "30303")]
        int P2PPort { get; set; }

        [ConfigItem(DisabledForCli = true, HiddenFromDocs = true, DefaultValue = "2000")]
        int MaxPersistedPeerCount { get; }

        [ConfigItem(DisabledForCli = true, HiddenFromDocs = true, DefaultValue = "2200")]
        int PersistedPeerCountCleanupThreshold { get; set; }

        [ConfigItem(DisabledForCli = true, HiddenFromDocs = true, DefaultValue = "10000")]
        int MaxCandidatePeerCount { get; set; }

        [ConfigItem(DisabledForCli = true, HiddenFromDocs = true, DefaultValue = "11000")]
        int CandidatePeerCountCleanupThreshold { get; set; }

        [ConfigItem(DefaultValue = "false", Description = "Enabled very verbose diag network tracing files for DEV purposes (Nethermind specific)")]
        bool DiagTracerEnabled { get; set; }

        [ConfigItem(DefaultValue = "11", Description = "[TECHNICAL] Defines the size of a buffer allocated to each peer - default is 8192 << 11 so 16MB where order is 11.")]
        int NettyArenaOrder { get; set; }

        [ConfigItem(DefaultValue = "", Description = "Bootnodes")]
        string Bootnodes { get; set; }

        [ConfigItem(DefaultValue = "false", Description = "Enable automatic port forwarding via UPnP")]
        bool EnableUPnP { get; set; }

        [ConfigItem(DefaultValue = "0", HiddenFromDocs = true, Description = "[TECHNICAL] Introduce a fixed latency for all p2p message send. Useful for testing higher latency network or simulate slower network for testing purpose.")]
        long SimulateSendLatencyMs { get; set; }
    }
}
