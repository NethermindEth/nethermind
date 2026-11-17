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
/// new address, <see cref="Touch"/> refreshes an admitted node, and the exempt set identifies nodes that
/// bypass the filter. Admission decides who enters the routing table and the peer-candidate stream; it
/// deliberately does not decide whether a discovery message is answered.
/// <para>
/// Exemption is tracked by node identity rather than by the flags on the instance being admitted: a node
/// rebuilt from a packet carries no flags, so a flagged instance records its identity in the exempt set and
/// every later reconstruction of that node is recognized. Static and trusted nodes are also resolved from
/// their managers, which covers first contact before the node has ever been seen locally.
/// </para>
/// </remarks>
internal sealed class DiscoveryNodeAdmission(
    NodeFilter filter,
    bool enabled,
    IRoutingTable<Node, ValueHash256> routingTable,
    KademliaConfig<Node> kademliaConfig,
    INetworkStorage discoveryStorage,
    int admittedCapacity,
    ILogManager logManager,
    IStaticNodesManager? staticNodes = null,
    ITrustedNodesManager? trustedNodes = null)
{
    private readonly NodeFilter _filter = filter;
    private readonly bool _enabled = enabled;
    private readonly IRoutingTable<Node, ValueHash256> _routingTable = routingTable;
    private readonly int _admittedCapacity = Math.Max(1, admittedCapacity);
    private readonly RecentNodeFilter<PublicKey> _admitted = new(Math.Max(1, admittedCapacity));
    private readonly ConcurrentDictionary<PublicKey, IPAddress> _admittedAddresses = new();
    private readonly IStaticNodesManager? _staticNodes = staticNodes;
    private readonly ITrustedNodesManager? _trustedNodes = trustedNodes;
    private readonly ConcurrentDictionary<PublicKey, byte> _exemptNodeIds = enabled
        ? CreateExemptNodeIds(kademliaConfig, discoveryStorage, staticNodes, trustedNodes)
        : new();
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
    /// filter without consuming IP capacity. A known node refreshes its own address window, but a node that
    /// moves to a different address must pass the filter again for the new address: identity proves who the
    /// sender is, not that it owns an address in the subnet it now claims.
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

        if (!AdmitAddress(node))
        {
            if (_logger.IsTrace) _logger.Trace($"Discovery node filtered by recent IP: {node:hide}.");
            return false;
        }

        return true;
    }

    private bool RefreshKnownNode(Node node)
    {
        if (!TryGetAdmittedAddress(node.Id, out IPAddress admittedAddress))
        {
            return AdmitAddress(node);
        }

        if (_filter.HasSameFilterKey(admittedAddress, node.Address.Address))
        {
            Touch(node);
            return true;
        }

        // A known identity may only claim a new subnet when that subnet is still free; otherwise N
        // identities could each be admitted from a distinct subnet and then all move into one target subnet.
        if (!AdmitAddress(node))
        {
            if (_logger.IsTrace) _logger.Trace($"Discovery node filtered after changing its IP: {node:hide}.");
            return false;
        }

        return true;
    }

    private bool AdmitAddress(Node node)
    {
        IPAddress address = node.Address.Address;
        if (!_filter.TryAccept(address)) return false;

        _admitted.TryReserve(node.Id);
        RecordAdmittedAddress(node.Id, address);
        return true;
    }

    private bool TryGetAdmittedAddress(PublicKey nodeId, out IPAddress address)
    {
        if (_admittedAddresses.TryGetValue(nodeId, out address!)) return true;

        if (_routingTable.TryGet(nodeId.Hash.ValueHash256, out Node? knownNode))
        {
            address = knownNode.DiscoveryAddress.Address;
            return true;
        }

        address = IPAddress.None;
        return false;
    }

    private void RecordAdmittedAddress(PublicKey nodeId, IPAddress address)
    {
        _admittedAddresses[nodeId] = address;
        if (_admittedAddresses.Count <= _admittedCapacity * 2) return;

        // Keep the map bounded alongside the admitted identities it describes.
        foreach (KeyValuePair<PublicKey, IPAddress> entry in _admittedAddresses)
        {
            if (!_admitted.Contains(entry.Key))
            {
                _admittedAddresses.TryRemove(entry.Key, out _);
            }
        }
    }

    /// <summary>
    /// Refreshes the address window for a node that is already admitted under the same filter key.
    /// </summary>
    /// <remarks>
    /// Do not use this to admit a new address: <see cref="NodeFilter.Touch"/> inserts a missing entry, which
    /// would reserve the address without passing the admission check.
    /// </remarks>
    private void Touch(Node node)
    {
        if (!_enabled) return;
        _filter.Touch(node.Address.Address);
    }

    private bool IsSelf(Node node) => _selfId is { } selfId && node.Id == selfId;

    private bool IsExempt(Node node)
    {
        // Remember the identity so a node rebuilt from a packet, which carries no flags, stays exempt.
        if (node.IsBootnode || node.IsStatic || node.IsTrusted)
        {
            _exemptNodeIds.TryAdd(node.Id, 0);
            return true;
        }

        return _exemptNodeIds.ContainsKey(node.Id) || IsManagedExempt(node.Id);
    }

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

    private static ConcurrentDictionary<PublicKey, byte> CreateExemptNodeIds(
        KademliaConfig<Node> kademliaConfig,
        INetworkStorage discoveryStorage,
        IStaticNodesManager? staticNodes,
        ITrustedNodesManager? trustedNodes)
    {
        ConcurrentDictionary<PublicKey, byte> exempt = new();
        if (kademliaConfig.CurrentNodeId is { } self)
        {
            exempt.TryAdd(self.Id, 0);
        }

        foreach (Node bootNode in kademliaConfig.BootNodes)
        {
            exempt.TryAdd(bootNode.Id, 0);
        }

        // Nodes persisted before the upgrade keep their exemption. Filtering them at load could empty a
        // node's routing table on the first restart, which hurts bootstrapping more than the subnet
        // concentration this filter addresses.
        foreach (NetworkNode persistedNode in discoveryStorage.GetPersistedNodes())
        {
            exempt.TryAdd(persistedNode.NodeId, 0);
        }

        AddManagedIds(exempt, staticNodes?.Nodes);
        AddManagedIds(exempt, trustedNodes?.Nodes);
        return exempt;

        static void AddManagedIds(ConcurrentDictionary<PublicKey, byte> exempt, IEnumerable<NetworkNode>? nodes)
        {
            if (nodes is null) return;

            foreach (NetworkNode node in nodes)
            {
                exempt.TryAdd(node.NodeId, 0);
            }
        }
    }
}
