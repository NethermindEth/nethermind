// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net;
using Nethermind.Config;
using Nethermind.Core.Crypto;
using Nethermind.Kademlia;
using Nethermind.Logging;
using Nethermind.Network.Config;
using Nethermind.Stats.Model;

namespace Nethermind.Network.Discovery.Kademlia;

/// <summary>
/// Applies the configured recent-IP/subnet filter before a discovered node enters the routing table.
/// </summary>
/// <remarks>
/// Sits in front of <see cref="INodeHealthTracker{TNode}"/> so every admission path is covered: inbound
/// discovery messages, lookup results, and nodes fed in by external <see cref="INodeSource"/>s. Nodes already
/// in the routing table are refreshed rather than filtered, and self, bootnodes, static/trusted nodes, and
/// nodes loaded from the discovery database bypass the filter — the same exemptions the pre-Kademlia
/// DiscoveryManager applied.
/// </remarks>
internal sealed class FilteringNodeHealthTracker(
    INodeHealthTracker<Node> inner,
    IRoutingTable<Node, ValueHash256> routingTable,
    KademliaConfig<Node> kademliaConfig,
    NodeFilter filter,
    bool enabled,
    INetworkStorage discoveryStorage,
    ILogManager logManager) : INodeHealthTracker<Node>
{
    private readonly INodeHealthTracker<Node> _inner = inner;
    private readonly IRoutingTable<Node, ValueHash256> _routingTable = routingTable;
    private readonly NodeFilter _filter = filter;
    private readonly bool _enabled = enabled;
    private readonly ILogger _logger = logManager.GetClassLogger<FilteringNodeHealthTracker>();
    private readonly HashSet<PublicKey> _exemptNodeIds = CreateExemptNodeIds(kademliaConfig, discoveryStorage);

    public void OnIncomingMessageFrom(Node sender)
    {
        if (!_enabled || ShouldAccept(sender))
        {
            _inner.OnIncomingMessageFrom(sender);
        }
        else if (_logger.IsTrace)
        {
            _logger.Trace($"Discovery node filtered by recent IP: {sender:hide}.");
        }
    }

    public void OnRequestFailed(Node node) => _inner.OnRequestFailed(node);

    private bool ShouldAccept(Node node)
    {
        if (node.IsBootnode || node.IsStatic || node.IsTrusted || _exemptNodeIds.Contains(node.Id))
        {
            return true;
        }

        IPAddress address = node.Address.Address;
        if (_routingTable.TryGet(node.Id.Hash.ValueHash256, out _))
        {
            // Refresh an existing entry without consuming filter capacity for the address again.
            _filter.Touch(address);
            return true;
        }

        return _filter.TryAccept(address);
    }

    private static HashSet<PublicKey> CreateExemptNodeIds(KademliaConfig<Node> kademliaConfig, INetworkStorage discoveryStorage)
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

        foreach (NetworkNode persistedNode in discoveryStorage.GetPersistedNodes())
        {
            exempt.Add(persistedNode.NodeId);
        }

        return exempt;
    }
}
