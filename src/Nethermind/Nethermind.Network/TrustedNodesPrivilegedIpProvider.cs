// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using System.Net;

namespace Nethermind.Network;

/// <summary>
/// Privileged-IP provider backed by the trusted node source (<c>trusted-nodes.json</c> + the
/// <c>admin_addTrustedPeer</c> RPC) via <see cref="ITrustedNodesManager"/>.
/// </summary>
/// <remarks>
/// Trusted peers always connect even when the node is full (geth's <c>trustedConn</c> semantics), so their
/// inbound connections must bypass the recent-IP rate-limit filter. The manager set is read live on each
/// query, so <c>admin_addTrustedPeer</c>/<c>admin_removeTrustedPeer</c> are reflected without event wiring.
/// Trusted nodes have no <c>Network.*Peers</c> config equivalent, so the manager is the only source.
/// </remarks>
public sealed class TrustedNodesPrivilegedIpProvider(ITrustedNodesManager trustedNodesManager) : IPrivilegedIpProvider
{
    public bool IsPrivileged(IPAddress ip)
    {
        IPAddress normalized = Normalize(ip);
        return trustedNodesManager.Nodes.Any(n => Normalize(n.HostIp).Equals(normalized));
    }

    private static IPAddress Normalize(IPAddress ip) => ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip;
}
