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
/// Privileged-IP provider backed by the static node sources directly: the <c>Network.StaticPeers</c> config
/// and <see cref="IStaticNodesManager"/> (<c>static-nodes.json</c> + the <c>admin_addPeer</c> RPC).
/// </summary>
/// <remarks>
/// Config static peers are immutable at runtime, so their IPs are parsed once; the manager's set is read
/// live on each query, so <c>admin_addPeer</c>/<c>admin_removePeer</c> are reflected without any event wiring.
/// </remarks>
public sealed class StaticNodesPrivilegedIpProvider : IPrivilegedIpProvider
{
    private readonly IStaticNodesManager _staticNodesManager;
    private readonly HashSet<IPAddress> _configStaticIps;

    public StaticNodesPrivilegedIpProvider(IStaticNodesManager staticNodesManager, INetworkConfig networkConfig, ILogManager logManager)
    {
        _staticNodesManager = staticNodesManager;
        ILogger logger = logManager.GetClassLogger<StaticNodesPrivilegedIpProvider>();
        // Mirror NodesLoader: only enode entries from config become static peers.
        _configStaticIps = NetworkNode.ParseNodes(networkConfig.StaticPeers, logger)
            .Where(static n => n.IsEnode)
            .Select(static n => Normalize(n.HostIp))
            .ToHashSet();
    }

    public bool IsPrivileged(IPAddress ip)
    {
        IPAddress normalized = Normalize(ip);
        return _configStaticIps.Contains(normalized)
               || _staticNodesManager.Nodes.Any(n => Normalize(n.HostIp).Equals(normalized));
    }

    private static IPAddress Normalize(IPAddress ip) => ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip;
}
