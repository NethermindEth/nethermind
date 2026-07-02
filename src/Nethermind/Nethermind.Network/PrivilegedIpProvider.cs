// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using System.Net;
using Nethermind.Config;
using Nethermind.Logging;
using Nethermind.Network.Config;

namespace Nethermind.Network;

/// <summary>
/// Privileged-IP provider covering the must-keep node sources: static nodes (the <c>Network.StaticPeers</c>
/// config and <see cref="IStaticNodesManager"/>) and trusted nodes (<see cref="ITrustedNodesManager"/>).
/// </summary>
/// <remarks>
/// Their inbound connections bypass the recent-IP rate-limit filter so an operator-configured peer is never
/// throttled. Config static peers are immutable at runtime, so their IPs are parsed once; the manager sets are
/// read live on each query, so <c>admin_add/removePeer</c> and <c>admin_add/removeTrustedPeer</c> are reflected
/// without event wiring.
/// <para>
/// Matching is by IP address, not node id. A node's true identity is its public key, and in theory it may
/// present multiple IPs/ports, so IP-based privileging is approximate — but the recent-IP filter runs
/// pre-handshake, before the remote public key is known, so the IP is the only identifier available at that
/// point. A configured node connecting from an IP other than its advertised one would not be matched here.
/// </para>
/// </remarks>
public sealed class PrivilegedIpProvider : IPrivilegedIpProvider
{
    private readonly IStaticNodesManager _staticNodesManager;
    private readonly ITrustedNodesManager _trustedNodesManager;
    private readonly HashSet<IPAddress> _configStaticIps;

    public PrivilegedIpProvider(
        IStaticNodesManager staticNodesManager,
        ITrustedNodesManager trustedNodesManager,
        INetworkConfig networkConfig,
        ILogManager logManager)
    {
        _staticNodesManager = staticNodesManager;
        _trustedNodesManager = trustedNodesManager;
        ILogger logger = logManager.GetClassLogger<PrivilegedIpProvider>();
        // Mirror NodesLoader: only enode entries from config become static peers.
        _configStaticIps = NetworkNode.ParseNodes(networkConfig.StaticPeers, logger)
            .Where(static n => n.IsEnode)
            .Select(static n => Normalize(n.HostIp))
            .ToHashSet();
    }

    public bool IsPrivileged(IPAddress ip) =>
        _configStaticIps.Contains(Normalize(ip))
        || _staticNodesManager.ContainsIp(ip)
        || _trustedNodesManager.ContainsIp(ip);

    private static IPAddress Normalize(IPAddress ip) => ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip;
}
