// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
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
/// reason, and an addition stops the node renewing the reservation it still holds. Flags on an admitted
/// instance are evidence for that call only and never create a lasting entitlement.
/// </para>
/// <para>
/// Every routing-table node is persisted, so filtering persisted nodes at load could empty the routing
/// table and hurt bootstrapping more than the subnet concentration this filter addresses. A persisted
/// node is therefore exempt only at the endpoint that was persisted for it: discv4 stores the bonded
/// endpoint, which is the one the node was admitted at. An ENR record does not prove the endpoint it
/// advertises was admitted, so it goes through the filter like any other node. Identity alone does not
/// entitle an ordinary node to skip the filter after it reappears at another address, or persisted
/// identities from different subnets could all bypass the subnet limit once they move into one subnet
/// after a restart. An entry without a usable address cannot be exempted and is skipped.
/// </para>
/// </remarks>
internal sealed class DiscoveryNodeAdmission(
    NodeFilter filter,
    bool enabled,
    KademliaConfig<Node> kademliaConfig,
    ILogManager logManager,
    IStaticNodesManager? staticNodes = null,
    ITrustedNodesManager? trustedNodes = null,
    IEnumerable<NetworkNode>? configuredStaticNodes = null)
{
    private readonly NodeFilter _filter = filter;
    private readonly bool _enabled = enabled;
    private readonly IStaticNodesManager? _staticNodes = staticNodes;
    private readonly ITrustedNodesManager? _trustedNodes = trustedNodes;
    private readonly HashSet<PublicKey> _exemptNodeIds = enabled
        ? CreateExemptNodeIds(kademliaConfig, configuredStaticNodes)
        : [];
    private readonly ConcurrentDictionary<PublicKey, IPAddress> _persistedAddresses = new();
    private readonly ILogger _logger = logManager.GetClassLogger<DiscoveryNodeAdmission>();

    /// <summary>
    /// Records the node's address on first contact and returns whether it may be handled.
    /// </summary>
    /// <remarks>
    /// Safe to call from both the adapter gate and the routing-table decorator for the same message. Self and
    /// exempt nodes bypass the filter without creating a filter entry, and an exempt node does not extend a
    /// reservation it still holds. Every other node is decided by the address entry alone: identity proves who
    /// the sender is, not that it owns an address in the subnet it claims, so only the identity holding a live
    /// reservation may extend it, and a node that moves to a different address or whose window was taken over
    /// by another identity is filtered again. An identity is not remembered separately from its reservation.
    /// </remarks>
    public bool TryAdmit(Node node) => TryAdmitCore(node, trustInstanceFlags: true);

    /// <summary>
    /// Re-validates a node taken from a bucket's replacement cache before it is promoted into the bucket.
    /// </summary>
    /// <remarks>
    /// Runs while the routing table is locked, so unlike <see cref="TryAdmit"/> it takes no routing-table read; the
    /// inverse order would deadlock. The node is not an active entry yet, so the table probe has nothing to add, and
    /// the address filter is a leaf lock that no caller holds while entering the table. A node that no longer holds
    /// its reservation is filtered like any other node, and flags on the cached instance are ignored because the
    /// operator may have removed it from its manager since it was cached. Exemption is likewise resolved before the
    /// reservation is extended, so a node promoted after it became exempt stops renewing the reservation it still
    /// holds.
    /// </remarks>
    public bool TryAdmitPromoted(Node node) => TryAdmitCore(node, trustInstanceFlags: false);

    /// <summary>
    /// Records a node restored from discovery storage as exempt from the filter at its restored address.
    /// </summary>
    /// <remarks>
    /// Only an enode record persists the bonded endpoint the node was admitted at. An ENR may advertise an
    /// endpoint the node never answered a ping from, and a record written before the bonded endpoint was stored
    /// carries no admitted endpoint, so those nodes go through ordinary admission instead of being exempted.
    /// </remarks>
    /// <param name="node">The node restored from discovery storage.</param>
    public void AddPersistedNode(Node node)
    {
        if (_enabled && node.Enr is null) _persistedAddresses[node.Id] = node.Address.Address;
    }

    private bool TryAdmitCore(Node node, bool trustInstanceFlags)
    {
        if (!_enabled) return true;

        // Exempt nodes never create a reservation; a node that becomes exempt while it still holds one must stop
        // renewing it, or a privileged node in a subnet would keep ordinary nodes out indefinitely. The live
        // exemption is therefore resolved before the reservation is extended. Self is in the configuration-backed
        // set, so it needs no separate check.
        if (IsExempt(node, trustInstanceFlags) || IsManagedExempt(node.Id)) return true;

        if (AdmitAddress(node)) return true;

        if (_logger.IsTrace)
        {
            if (trustInstanceFlags)
                _logger.Trace($"Discovery node filtered by recent IP: {node:hide}.");
            else
                _logger.Trace($"Discovery node filtered by recent IP on replacement promotion: {node:hide}.");
        }

        return false;
    }

    // Accepting and extending happen in one filter lock scope, so a concurrent admission for the same identity
    // cannot evict the entry between the two and reject the node against a reservation it already owns.
    private bool AdmitAddress(Node node) => _filter.TryAcceptOrRefresh(node.Address.Address, node.Id);

    // Instance flags are evidence for the call that carries them only. TryAdmit sees the node's current flags, but
    // a promotion re-checks an instance cached minutes earlier, whose flags may have gone stale since the operator
    // could have removed it from its manager while it waited. Caching the flags by identity would keep the exemption
    // alive after that removal; lasting exemptions come from the configuration-backed set and live manager
    // membership, resolved by IsManagedExempt.
    private bool IsExempt(Node node, bool trustInstanceFlags)
        => _exemptNodeIds.Contains(node.Id)
           || (trustInstanceFlags && (node.IsBootnode || node.IsStatic || node.IsTrusted))
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
