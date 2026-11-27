// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using Nethermind.Core.Crypto;
using Nethermind.Kademlia;
using Nethermind.Network.Config;
using Nethermind.Stats.Model;

namespace Nethermind.Network.Discovery.Kademlia;

/// <summary>
/// Limits how many discv4 routing-table entries a single IP subnet may hold.
/// </summary>
/// <remarks>
/// Limits follow geth v1.16.5 <c>table.go</c>: <c>tableIPLimit = 10</c> and <c>bucketIPLimit = 2</c>. The subnet
/// key reuses <see cref="NodeFilter.IpSubnetKey"/> (IPv4 /24, IPv6 /64) for public addresses; loopback,
/// private (incl. CGNAT 100.64.0.0/10), link-local and IPv6 ULA addresses bypass the limiter entirely
/// (geth <c>table.go:addIP</c> <c>AddrIsLAN</c> parity), so the limiter adds no filtering state of its own: <see cref="_counts"/> is bounded by routing-table
/// membership and <see cref="_held"/> mirrors it per identity.
/// <para>
/// Only active entries are counted. Identity exemptions (self, bootnodes, static, trusted)
/// are evaluated first and keep their slots.
/// All state is guarded by a private lock; the lock order is routing-table lock,
/// then this lock. <see cref="TryAdmit"/> and <see cref="Release"/> run under the routing-table lock, while
/// <see cref="WouldAdmit"/> runs on the peer-candidate consumer thread — the lock is what makes that probe safe.
/// The limiter never calls back into the table.
/// </para>
/// </remarks>
internal sealed class DiscoveryNodeLimiter : IRoutingTableNodeLimiter<Node>
{
    /// <summary>Maximum number of active entries with the same subnet key in the whole table.</summary>
    internal const int TableLimit = 10;

    /// <summary>Maximum number of entries with the same subnet key in one bucket, excluding the entry itself.</summary>
    internal const int BucketLimit = 2;

    private readonly Lock _lock = new();
    private readonly Dictionary<NodeFilter.IpSubnetKey, int> _counts = [];
    private readonly Dictionary<PublicKey, NodeFilter.IpSubnetKey> _held = [];
    private readonly HashSet<PublicKey> _bootNodeIds = [];
    private readonly PublicKey _currentNodeId;
    private readonly INetworkConfig _networkConfig;
    private readonly IDiscoveryNodeExemptions? _exemptions;

    public DiscoveryNodeLimiter(
        Node currentNode,
        IReadOnlyList<Node> bootNodes,
        INetworkConfig networkConfig,
        IDiscoveryNodeExemptions? exemptions)
    {
        _currentNodeId = currentNode.Id;
        _networkConfig = networkConfig;
        _exemptions = exemptions;

        foreach (Node bootNode in bootNodes)
        {
            _bootNodeIds.Add(bootNode.Id);
        }
    }

    /// <inheritdoc/>
    public bool TryAdmit(Node node, ReadOnlySpan<Node> bucketNodes)
    {
        lock (_lock)
        {
            return TryAdmitLocked(node, bucketNodes);
        }
    }

    private bool TryAdmitLocked(Node node, ReadOnlySpan<Node> bucketNodes)
    {
        if (!_networkConfig.FilterDiscoveryNodesByRecentIp) return true;

        // Exemptions are evaluated before any count: an exempt node never consumes a subnet slot.
        if (IsExempt(node))
        {
            ReleaseLocked(node);
            return true;
        }

        if (GetKey(node) is not { } key)
        {
            // A public->LAN move must not leave the old subnet charged; mirrors the exemption path above.
            ReleaseLocked(node);
            return true;
        }

        // A refresh of the identity in place neither consumes nor re-checks a slot.
        if (_held.TryGetValue(node.Id, out NodeFilter.IpSubnetKey existingKey) && existingKey.Equals(key)) return true;

        if (CountInBucket(bucketNodes, key, node.Id) >= BucketLimit) return false;
        if (_counts.TryGetValue(key, out int tableCount) && tableCount >= TableLimit) return false;

        // All checks passed before mutating, so a refused address change keeps the old endpoint and charge.
        _counts[key] = tableCount + 1;
        if (_held.TryGetValue(node.Id, out NodeFilter.IpSubnetKey previousKey))
        {
            Decrement(previousKey);
        }

        _held[node.Id] = key;
        return true;
    }

    /// <inheritdoc/>
    public void Release(Node node)
    {
        lock (_lock)
        {
            ReleaseLocked(node);
        }
    }

    private void ReleaseLocked(Node node)
    {
        if (!_held.Remove(node.Id, out NodeFilter.IpSubnetKey key)) return;

        Decrement(key);
    }

    /// <inheritdoc/>
    public bool WouldAdmit(Node node)
    {
        lock (_lock)
        {
            return WouldAdmitLocked(node);
        }
    }

    private bool WouldAdmitLocked(Node node)
    {
        if (!_networkConfig.FilterDiscoveryNodesByRecentIp) return true;
        if (IsExempt(node)) return true;

        // Mirrors TryAdmitLocked: a candidate the table would reject must not reach the candidate stream.
        if (GetKey(node) is not { } key) return true;

        if (_held.TryGetValue(node.Id, out NodeFilter.IpSubnetKey existingKey) && existingKey.Equals(key)) return true;

        return !_counts.TryGetValue(key, out int tableCount) || tableCount < TableLimit;
    }

    private int CountInBucket(ReadOnlySpan<Node> bucketNodes, NodeFilter.IpSubnetKey key, PublicKey identity)
    {
        int count = 0;
        foreach (Node bucketNode in bucketNodes)
        {
            // Identity first (cheapest), then the key, and the exemption check only on a key match.
            if (bucketNode.Id.Equals(identity)) continue;
            if (GetKey(bucketNode) is not { } bucketKey || !bucketKey.Equals(key)) continue;
            if (IsExempt(bucketNode)) continue;
            if (++count >= BucketLimit) break;
        }

        return count;
    }

    private void Decrement(NodeFilter.IpSubnetKey key)
    {
        if (!_counts.TryGetValue(key, out int count)) return;

        if (count <= 1)
        {
            _counts.Remove(key);
        }
        else
        {
            _counts[key] = count - 1;
        }
    }

    private NodeFilter.IpSubnetKey? GetKey(Node node)
    {
        IPEndPoint? address = node.Address;
        if (address is null) return null;

        // Matches geth table.go:addIP AddrIsLAN bypass (loopback, private, link-local, IPv6 ULA).
        // Nethermind additionally treats CGNAT 100.64.0.0/10 as LAN, consistent with NodeFilter.DefaultKey.
        if (address.Address.IsLoopbackOrPrivateOrLinkLocal) return null;

        // Same-subnet filtering disabled means every public address is keyed exactly, matching NodeFilter.
        if (!_networkConfig.FilterDiscoveryNodesBySameSubnet) return NodeFilter.IpSubnetKey.Exact(address.Address);

        // Deliberately DefaultKey, not CreateNodeFilterKey with the resolved external IP: the latter's
        // same-subnet-as-self refinement requires a local current IP sharing the remote's /24 or /64, which
        // makes the remote local too — and local remotes are already keyed exactly. The resolved address can
        // therefore never change the outcome (short of an external IP of exactly ::1), so depending on the
        // resolver would only add resolution-state drift with no observable effect.
        return NodeFilter.IpSubnetKey.DefaultKey(address.Address);
    }

    /// <summary>
    /// Identity exemptions: self, bootnodes, and static/trusted nodes by flag or manager. Never consumes a
    /// subnet slot.
    /// </summary>
    private bool IsExempt(Node node)
    {
        PublicKey id = node.Id;
        if (id.Equals(_currentNodeId) || _bootNodeIds.Contains(id)) return true;
        if (node.IsStatic || node.IsTrusted) return true;

        return _exemptions?.IsExempt(id) is true;
    }
}
