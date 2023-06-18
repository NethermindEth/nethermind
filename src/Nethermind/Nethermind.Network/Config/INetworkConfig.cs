// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;

namespace Nethermind.Network.Config
{
    public interface INetworkConfig : IConfig
    {
        public const int DefaultNettyArenaOrder = -1;
        public const int MaxNettyArenaOrder = 14;
        public const int DefaultMaxNettyArenaCount = 8;

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

        [ConfigItem(DefaultValue = "-1", Description = "[TECHNICAL] Defines the size of a netty arena order. Default depends on memory hint.")]
        int NettyArenaOrder { get; set; }

        [ConfigItem(DefaultValue = "8", Description = "[TECHNICAL] Defines maximum netty arena count. Increasing this on high core machine without increasing memory budget may reduce chunk size so much that it causes significant netty huge allocation.")]
        uint MaxNettyArenaCount { get; set; }

        [ConfigItem(DefaultValue = "", Description = "Bootnodes")]
        string Bootnodes { get; set; }

        [ConfigItem(DefaultValue = "false", Description = "Enable automatic port forwarding via UPnP")]
        bool EnableUPnP { get; set; }

        [ConfigItem(DefaultValue = "0", HiddenFromDocs = true, Description = "[TECHNICAL] Introduce a fixed latency for all p2p message send. Useful for testing higher latency network or simulate slower network for testing purpose.")]
        long SimulateSendLatencyMs { get; set; }

        [ConfigItem(DefaultValue = "0", HiddenFromDocs = true, Description = "[TECHNICAL] Number of concurrent outgoing connections. Reduce this if your ISP throttles from having open too many connections. Default is 0 which means same as processor count.")]
        int NumConcurrentOutgoingConnects { get; set; }

        [ConfigItem(DefaultValue = "2000", HiddenFromDocs = true, Description = "[TECHNICAL] Outgoing connection timeout in ms. Default is 2 seconds.")]
        int ConnectTimeoutMs { get; set; }

        [ConfigItem(DefaultValue = "1", HiddenFromDocs = true, Description = "[TECHNICAL] Num of thread in final processing of network packet. Set to more than 1 if you have very fast internet.")]
        int ProcessingThreadCount { get; set; }


        [ConfigItem(DefaultValue = "2000", HiddenFromDocs = true, Description = "[TECHNICAL] Max snap response latency before reducing snap request size.")]
        long SnapResponseLatencyHighWatermarkMs { get; set; }

        [ConfigItem(DefaultValue = "1000", HiddenFromDocs = true, Description = "[TECHNICAL] Min snap response latency before increasing snap request size.")]
        long SnapResponseLatencyLowWatermarkMs { get; set; }

        [ConfigItem(DefaultValue = "2000000", HiddenFromDocs = true, Description = "[TECHNICAL] Max snap request size.")]
        int SnapRequestMaxBytes { get; set; }

        [ConfigItem(DefaultValue = "20000", HiddenFromDocs = true, Description = "[TECHNICAL] Min snap request size.")]
        int SnapRequestMinBytes { get; set; }
    }
}
