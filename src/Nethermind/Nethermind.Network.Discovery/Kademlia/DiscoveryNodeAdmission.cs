// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net;
using System.Threading;
using Nethermind.Config;
using Nethermind.Core.Crypto;
using Nethermind.Kademlia;
using Nethermind.Logging;
using Nethermind.Network;
using Nethermind.Stats.Model;

namespace Nethermind.Network.Discovery.Kademlia;

/// <summary>
/// Single authoritative admission decision for discv4 discovery nodes.
/// </summary>
/// <remarks>
/// The IP filter is stateful, so the operations are deliberately distinct: <see cref="WouldAccept"/> is
/// read-only, <see cref="TryAdmit"/> is the one call that records a new address, <see cref="Touch"/>
/// refreshes an admitted node, and <see cref="IsAdmitted"/> answers whether this exact node already passed
/// admission.
/// </remarks>
internal sealed class DiscoveryNodeAdmission(
    NodeFilter filter,
    bool enabled,
    IRoutingTable<Node, ValueHash256> routingTable,
    KademliaConfig<Node> kademliaConfig,
    INetworkStorage discoveryStorage,
    int admittedCapacity,
    ILogManager logManager)
{
    private readonly NodeFilter _filter = filter;
    private readonly bool _enabled = enabled;
    private readonly IRoutingTable<Node, ValueHash256> _routingTable = routingTable;
    private readonly RecentNodeFilter<PublicKey> _admitted = new(Math.Max(1, admittedCapacity));
    private readonly HashSet<PublicKey> _exemptNodeIds = enabled
        ? CreateExemptNodeIds(kademliaConfig, discoveryStorage)
        : [];
    private readonly PublicKey? _selfId = kademliaConfig.CurrentNodeId?.Id;
    private readonly Lock _admitLock = new();
    private readonly ILogger _logger = logManager.GetClassLogger<DiscoveryNodeAdmission>();

    /// <summary>
    /// Whether discovery-node admission filtering is enabled.
    /// </summary>
    public bool Enabled => _enabled;

    /// <summary>
    /// Records the node's address on first contact and returns whether it may be handled.
    /// </summary>
    /// <remarks>
    /// Safe to call from both the adapter gate and the routing-table decorator for the same message: a node
    /// admitted by the first call is recognized as known by the second. Self and exempt nodes bypass the
    /// filter without consuming IP capacity. Already-known nodes refresh their address window through
    /// <see cref="Touch"/>, so the subnet stays reserved for as long as the node remains active.
    /// </remarks>
    public bool TryAdmit(Node node)
    {
        if (!_enabled) return true;

        // Self and exempt nodes must not consume the filter's IP capacity.
        if (IsSelf(node) || IsExempt(node))
        {
            _admitted.TryReserve(node.Id);
            return true;
        }

        // Known nodes keep the refresh behavior of the old lifecycle-manager dictionary.
        if (IsKnown(node))
        {
            Touch(node);
            return true;
        }

        using Lock.Scope scope = _admitLock.EnterScope();
        // Another thread may have admitted this node while we waited for the lock.
        if (IsExempt(node))
        {
            _admitted.TryReserve(node.Id);
            return true;
        }

        if (IsKnown(node))
        {
            Touch(node);
            return true;
        }

        if (!_filter.TryAccept(node.Address.Address))
        {
            if (_logger.IsTrace) _logger.Trace($"Discovery node filtered by recent IP: {node:hide}.");
            return false;
        }

        _admitted.TryReserve(node.Id);
        return true;
    }

    /// <summary>
    /// Read-only: would this node's address be accepted if it were new? Does not consume capacity.
    /// </summary>
    /// <remarks>
    /// Used to gate outbound probes. Because it never records an address, several nodes from a subnet that
    /// has not been recorded yet may be probed before the first reply is admitted.
    /// </remarks>
    public bool WouldAccept(Node node)
    {
        if (!_enabled) return true;
        if (IsSelf(node) || IsExempt(node) || IsKnown(node)) return true;
        return _filter.WouldAccept(node.Address.Address);
    }

    /// <summary>
    /// Read-only: has this exact node already passed <see cref="TryAdmit"/> (or is it exempt/known)?
    /// </summary>
    public bool IsAdmitted(Node node)
    {
        if (!_enabled) return true;
        return IsSelf(node) || IsExempt(node) || IsKnown(node);
    }

    /// <summary>
    /// Refreshes the address window for a node that is already admitted.
    /// </summary>
    public void Touch(Node node)
    {
        if (!_enabled) return;
        _filter.Touch(node.Address.Address);
    }

    private bool IsSelf(Node node) => _selfId is { } selfId && node.Id == selfId;

    private bool IsExempt(Node node)
        => node.IsBootnode
           || node.IsStatic
           || node.IsTrusted
           || _exemptNodeIds.Contains(node.Id);

    private bool IsKnown(Node node)
        => _admitted.Contains(node.Id)
           || _routingTable.TryGet(node.Id.Hash.ValueHash256, out _);

    private static HashSet<PublicKey> CreateExemptNodeIds(
        KademliaConfig<Node> kademliaConfig,
        INetworkStorage discoveryStorage)
    {
        HashSet<PublicKey> exempt = new(kademliaConfig.BootNodes.Count + 1);
        if (kademliaConfig.CurrentNodeId is { } self)
        {
            exempt.Add(self.Id);
        }

        foreach (Node bootNode in kademliaConfig.BootNodes)
        {
            exempt.Add(bootNode.Id);
        }

        // Nodes persisted before the upgrade keep their exemption. Filtering them at load could empty a
        // node's routing table on the first restart, which hurts bootstrapping more than the subnet
        // concentration this filter addresses.
        foreach (NetworkNode persistedNode in discoveryStorage.GetPersistedNodes())
        {
            exempt.Add(persistedNode.NodeId);
        }

        return exempt;
    }
}
