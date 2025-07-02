// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;
using Nethermind.Core;

namespace Nethermind.Network.Config;

public interface INetworkConfig : IConfig
{
    public const int DefaultNettyArenaOrder = -1;
    public const int MaxNettyArenaOrder = 14;
    public const int DefaultMaxNettyArenaCount = 8;

    [ConfigItem(Description = "The external IP. Use only when the external IP cannot be resolved automatically.", DefaultValue = "null")]
    string? ExternalIp { get; set; }

    [ConfigItem(Description = "The local IP. Use only when the local IP cannot be resolved automatically.", DefaultValue = "null")]
    string? LocalIp { get; set; }

    [ConfigItem(Description = $"A list of peers to keep connection for. Static peers are affected by `{nameof(MaxActivePeers)}`.", DefaultValue = "null")]
    string? StaticPeers { get; set; }

    [ConfigItem(Description = "Use tree is available through a DNS name. For the default of `<chain name>.ethdisco.net`, leave unspecified.", DefaultValue = "null")]
    string? DiscoveryDns { get; set; }

    [ConfigItem(Description = "Whether to use static peers only.", DefaultValue = "false")]
    bool OnlyStaticPeers { get; set; }

    [ConfigItem(DisabledForCli = true, HiddenFromDocs = true, Description = "If 'false' then discovered node list will be cleared on each restart.", DefaultValue = "true")]
    bool IsPeersPersistenceOn { get; set; }

    [ConfigItem(Description = $"Deprecated. Use `{nameof(MaxActivePeers)}` instead. The max number of connected peers.", DefaultValue = "50", HiddenFromDocs = true)]
    int ActivePeersMaxCount { get; set; }

    [ConfigItem(Description = "The max number of priority peers. Can be overridden by a plugin.", DefaultValue = "0")]
    int PriorityPeersMaxCount { get; set; }

    [ConfigItem(Description = "The max allowed number of connected peers.", DefaultValue = "50")]
    int MaxActivePeers { get; set; }

    [ConfigItem(DisabledForCli = true, HiddenFromDocs = true, DefaultValue = "5000")]
    int PeersPersistenceInterval { get; set; }

    [ConfigItem(DisabledForCli = true, HiddenFromDocs = true, DefaultValue = "250")]
    int PeersUpdateInterval { get; set; }

    [ConfigItem(DisabledForCli = true, HiddenFromDocs = true, DefaultValue = "10000")]
    int P2PPingInterval { get; }

    [ConfigItem(Description = $"The UDP port number for incoming discovery connections. It's recommended to keep it the same as the TCP port (`{nameof(P2PPort)}`) because other values have not been tested yet.", DefaultValue = "30303", IsPortOption = true)]
    int DiscoveryPort { get; set; }

    [ConfigItem(Description = "The TCP port for incoming P2P connections.", DefaultValue = "30303", IsPortOption = true)]
    int P2PPort { get; set; }

    [ConfigItem(DisabledForCli = true, HiddenFromDocs = true, DefaultValue = "2000")]
    int MaxPersistedPeerCount { get; }

    [ConfigItem(DisabledForCli = true, HiddenFromDocs = true, DefaultValue = "2200")]
    int PersistedPeerCountCleanupThreshold { get; set; }

    [ConfigItem(DisabledForCli = true, HiddenFromDocs = true, DefaultValue = "10000")]
    int MaxCandidatePeerCount { get; set; }

    [ConfigItem(DisabledForCli = true, HiddenFromDocs = true, DefaultValue = "11000")]
    int CandidatePeerCountCleanupThreshold { get; set; }

    [ConfigItem(DefaultValue = "false", Description = "Whether to enable a verbose diagnostic tracing.")]
    bool DiagTracerEnabled { get; set; }

    [ConfigItem(DefaultValue = "-1", Description = "The size of the DotNetty arena order. `-1` to depend on the memory hint.")]
    int NettyArenaOrder { get; set; }

    [ConfigItem(DefaultValue = "8", Description = "The maximum DotNetty arena count. Increasing this on a high-core CPU without increasing the memory budget may reduce chunk size so much that it causes a huge memory allocation.")]
    uint MaxNettyArenaCount { get; set; }

    [ConfigItem(DefaultValue = "", Description = "A comma-separated enode list to be used as boot nodes.")]
    string Bootnodes { get; set; }

    [ConfigItem(DefaultValue = "false", Description = "Whether to enable automatic port forwarding via UPnP.")]
    bool EnableUPnP { get; set; }

    [ConfigItem(DefaultValue = "0", HiddenFromDocs = true, Description = "[TECHNICAL] Introduce a fixed latency for all p2p message send. Useful for testing higher latency network or simulate slower network for testing purpose.")]
    long SimulateSendLatencyMs { get; set; }

    [ConfigItem(DefaultValue = "0", HiddenFromDocs = true, Description = "[TECHNICAL] Number of concurrent outgoing connections. Reduce this if your ISP throttles from having open too many connections. Default is 0 which means same as processor count.")]
    int NumConcurrentOutgoingConnects { get; set; }

    [ConfigItem(DefaultValue = "20", HiddenFromDocs = true, Description = "[TECHNICAL] Max number of new outgoing connections per second. Default is 20.")]
    int MaxOutgoingConnectPerSec { get; set; }

    [ConfigItem(DefaultValue = "2000", HiddenFromDocs = true, Description = "[TECHNICAL] Outgoing connection timeout in ms. Default is 2 seconds.")]
    int ConnectTimeoutMs { get; set; }

    [ConfigItem(DefaultValue = "1", HiddenFromDocs = true, Description = "[TECHNICAL] Num of thread in final processing of network packet. Set to more than 1 if you have very fast internet.")]
    int ProcessingThreadCount { get; set; }

    [ConfigItem(DefaultValue = null, HiddenFromDocs = true, Description = "[TECHNICAL] Only allow peer with clientId matching this regex. Useful for testing. eg: 'besu' to only connect to BeSU")]
    string? ClientIdMatcher { get; set; }

    [ConfigItem(DefaultValue = "false", HiddenFromDocs = true, Description = "[TECHNICAL] Disable feeding ENR DNS records to discv4 table")]
    bool DisableDiscV4DnsFeeder { get; set; }

    [ConfigItem(DefaultValue = "false", HiddenFromDocs = true, Description = "[TECHNICAL] Shutdown timeout when closing TCP port.")]
    long RlpxHostShutdownCloseTimeoutMs { get; set; }

    [ConfigItem(DefaultValue = ProductInfo.DefaultPublicClientIdFormat, Description = "A template string for the public client id provided to external clients. Allowed placeholders: `{name}` `{version}` `{os}` `{runtime}`.")]
    string PublicClientIdFormat { get; set; }

    [ConfigItem(DefaultValue = "true", Description = "Enable Enr discovery", HiddenFromDocs = true)]
    bool EnableEnrDiscovery { get; set; }
}
