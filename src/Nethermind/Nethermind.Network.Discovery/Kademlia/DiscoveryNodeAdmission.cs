// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.Collections.Generic;
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
/// The IP filter is stateful and keyed by address, so <see cref="TryAdmit"/> is the one call that records a
/// new address and reserves it for the sending identity, a refresh extends that reservation only while the
/// same identity still holds it, and the exempt set identifies nodes that bypass the filter. The filter
/// instance belongs to this class: callers that do not claim ownership use their own instance, or they would
/// compete for discovery's entries. Admission decides who enters the routing table and the peer-candidate
/// stream; it deliberately does not decide whether a discovery message is answered.
/// <para>
/// Exemption is tracked by node identity rather than by the flags on the instance being admitted, because
/// a node rebuilt from a packet carries no flags. The identity set is built once from configuration: self,
/// bootnodes, and configured static peers. Static and trusted nodes are resolved live from their managers,
/// so a removal is observed on the next admission unless the identity is also exempt for an independent
/// reason. Flags on an admitted instance are evidence for that call only and never create a lasting
/// entitlement.
/// </para>
/// <para>
/// Every routing-table node is persisted, so filtering persisted nodes at load could empty the routing
/// table and hurt bootstrapping more than the subnet concentration this filter addresses. A persisted
/// node is therefore exempt only at the address it was restored with: identity alone does not entitle an
/// ordinary node to skip the filter after it reappears at another address, or persisted identities from
/// different subnets could all bypass the subnet limit once they move into one subnet after a restart.
/// An entry without a usable address cannot be exempted and is skipped, so it goes through the filter
/// like any other node.
/// </para>
/// </remarks>
internal sealed class DiscoveryNodeAdmission(
    NodeFilter filter,
    bool enabled,
    IRoutingTable<Node, ValueHash256> routingTable,
    KademliaConfig<Node> kademliaConfig,
    int admittedCapacity,
    ILogManager logManager,
    IStaticNodesManager? staticNodes = null,
    ITrustedNodesManager? trustedNodes = null,
    IEnumerable<NetworkNode>? configuredStaticNodes = null)
{
    private readonly NodeFilter _filter = filter;
    private readonly bool _enabled = enabled;
    private readonly IRoutingTable<Node, ValueHash256> _routingTable = routingTable;
    private readonly RecentNodeFilter<PublicKey> _admitted = new(Math.Max(1, admittedCapacity));
    private readonly IStaticNodesManager? _staticNodes = staticNodes;
    private readonly ITrustedNodesManager? _trustedNodes = trustedNodes;
    private readonly HashSet<PublicKey> _exemptNodeIds = enabled
        ? CreateExemptNodeIds(kademliaConfig, configuredStaticNodes)
        : [];
    private readonly ConcurrentDictionary<PublicKey, IPAddress> _persistedAddresses = new();
    private readonly PublicKey? _selfId = kademliaConfig.CurrentNodeId?.Id;
    private readonly Lock _admitLock = new();
    private readonly ILogger _logger = logManager.GetClassLogger<DiscoveryNodeAdmission>();

    /// <summary>
    /// Records the node's address on first contact and returns whether it may be handled.
    /// </summary>
    /// <remarks>
    /// Safe to call from both the adapter gate and the routing-table decorator for the same message: a node
    /// admitted by the first call is recognized as known by the second. Self and exempt nodes bypass the
    /// filter without creating a filter entry. A known node refreshes its own address window, but only while it
    /// still holds that reservation: identity proves who the sender is, not that it owns an address in the
    /// subnet it now claims, so a node that moves to a different address or whose window was taken over by
    /// another identity must pass the filter again. The admitted cache is smaller than the address filter, so
    /// an identity can be evicted while its reservation is still live; that node renews the reservation it
    /// still owns instead of being rejected by it.
    /// </remarks>
    public bool TryAdmit(Node node)
    {
        if (!_enabled) return true;

        // A known node that still owns its address reservation has already passed the filter, so it needs no
        // exemption check. Exempt nodes never create a reservation, so they still reach IsExempt before
        // AdmitAddress could create one; a node that became exempt while it still held one keeps refreshing it
        // until it is evicted or expires.
        if (IsKnown(node) && _filter.TryRefresh(node.Address.Address, node.Id))
        {
            return true;
        }

        // Self and exempt nodes must not create a filter entry.
        if (IsSelf(node) || IsExempt(node))
        {
            _admitted.TryReserve(node.Id);
            return true;
        }

        // Known nodes keep the refresh behavior of the old lifecycle-manager dictionary.
        if (IsKnown(node))
        {
            return RefreshKnownNode(node);
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
            return RefreshKnownNode(node);
        }

        // The identity cache can evict a node that still holds a live address reservation, and a full routing
        // bucket keeps it out of the routing table, so it arrives here as unknown. TryAccept rejects any live
        // entry regardless of who holds it, so let the owner extend its own reservation instead.
        if (_filter.TryRefresh(node.Address.Address, node.Id))
        {
            _admitted.TryReserve(node.Id);
            return true;
        }

        if (!AdmitAddress(node))
        {
            if (_logger.IsTrace) _logger.Trace($"Discovery node filtered by recent IP: {node:hide}.");
            return false;
        }

        return true;
    }

    /// <summary>
    /// Records a node restored from discovery storage as exempt from the filter at its restored address.
    /// </summary>
    /// <param name="node">The node restored from discovery storage.</param>
    public void AddPersistedNode(Node node)
    {
        if (_enabled) _persistedAddresses[node.Id] = node.Address.Address;
    }

    private bool RefreshKnownNode(Node node)
    {
        // Only the identity holding the live reservation may extend it. A returning identity whose window
        // was taken over by another node must pass the same admission check as any other address.
        if (_filter.TryRefresh(node.Address.Address, node.Id)) return true;

        if (!AdmitAddress(node))
        {
            if (_logger.IsTrace) _logger.Trace($"Discovery node filtered after losing its address admission: {node:hide}.");
            return false;
        }

        return true;
    }

    private bool AdmitAddress(Node node)
    {
        IPAddress address = node.Address.Address;
        if (!_filter.TryAccept(address, node.Id)) return false;

        _admitted.TryReserve(node.Id);
        return true;
    }

    private bool IsSelf(Node node) => _selfId is { } selfId && node.Id == selfId;

    // Instance flags are evidence for this call only. Caching them by identity would keep the exemption
    // alive after the operator removes the node from its manager; lasting exemptions come from the
    // configuration-backed set, and manager membership is re-read here.
    private bool IsExempt(Node node)
        => _exemptNodeIds.Contains(node.Id)
           || node.IsBootnode
           || node.IsStatic
           || node.IsTrusted
           || IsManagedExempt(node.Id)
           || IsPersistedAtSameAddress(node);

    private bool IsPersistedAtSameAddress(Node node)
        => _persistedAddresses.TryGetValue(node.Id, out IPAddress? persistedAddress)
           && persistedAddress.Equals(node.Address.Address);

    private bool IsManagedExempt(PublicKey nodeId)
    {
        if (_staticNodes is not null)
        {
            foreach (NetworkNode node in _staticNodes.Nodes)
            {
                if (node.NodeId.Equals(nodeId)) return true;
            }
        }

        if (_trustedNodes is not null)
        {
            foreach (NetworkNode node in _trustedNodes.Nodes)
            {
                if (node.NodeId.Equals(nodeId)) return true;
            }
        }

        return false;
    }

    private bool IsKnown(Node node)
        => _admitted.Contains(node.Id)
           || _routingTable.TryGet(node.Id.Hash.ValueHash256, out _);

    private static HashSet<PublicKey> CreateExemptNodeIds(
        KademliaConfig<Node> kademliaConfig,
        IEnumerable<NetworkNode>? configuredStaticNodes)
    {
        HashSet<PublicKey> exempt = [];
        if (kademliaConfig.CurrentNodeId is { } self)
        {
            exempt.Add(self.Id);
        }

        foreach (Node bootNode in kademliaConfig.BootNodes)
        {
            exempt.Add(bootNode.Id);
        }

        if (configuredStaticNodes is not null)
        {
            foreach (NetworkNode node in configuredStaticNodes)
            {
                // Mirror NodesLoader: only enode entries from Network.StaticPeers become static peers.
                if (node.IsEnode) exempt.Add(node.NodeId);
            }
        }

        return exempt;
    }
}
