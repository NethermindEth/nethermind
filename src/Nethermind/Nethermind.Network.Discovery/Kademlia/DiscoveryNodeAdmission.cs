// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.Net;
using Nethermind.Config;
using Nethermind.Core.Crypto;
using Nethermind.Kademlia;
using Nethermind.Logging;
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
/// reason, and an addition stops the node renewing the reservation it still holds. Only the bootnode flag is
/// used as instance evidence, because it comes from configuration; a static or trusted flag can be stale by
/// the time it is seen, as the operator may have removed the node from its manager in between.
/// </para>
/// <para>
/// Every routing-table node is persisted, so filtering persisted nodes at load could empty the routing
/// table and hurt bootstrapping more than the subnet concentration this filter addresses. A persisted
/// node is therefore exempt only at the endpoint that was persisted for it: discv4 stores the bonded
/// endpoint, which is the one the node was admitted at. An ENR record does not prove the endpoint it
/// advertises was admitted, so it goes through the filter like any other node; a record that keeps the
/// signed ENR next to its bonded endpoint for address-family fallback earns no exemption for it either.
/// Identity alone does not entitle an ordinary node to skip the filter after it reappears at another
/// address, or persisted identities from different subnets could all bypass the subnet limit once they
/// move into one subnet after a restart. An entry without a usable address cannot be exempted and is
/// skipped.
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
    public bool TryAdmit(Node node) => TryAdmitCore(node, trustInstanceFlags: true, rejectionContext: null);

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
    public bool TryAdmitPromoted(Node node) => TryAdmitCore(node, trustInstanceFlags: false, " on replacement promotion");

    /// <summary>
    /// Re-validates a node already in the routing table before its refresh ping renews the entry.
    /// </summary>
    /// <remarks>
    /// The stored instance carries the flags it was admitted with, which may be stale if the operator removed it
    /// from a manager since, so instance flags are ignored exactly as in <see cref="TryAdmitPromoted"/>. Live
    /// exemptions are still resolved, and a node that no longer holds its reservation is filtered like any other.
    /// It reads no routing-table state, so it may run outside the table lock.
    /// </remarks>
    public bool TryAdmitRefresh(Node node) => TryAdmitCore(node, trustInstanceFlags: false, " on refresh");

    /// <summary>
    /// Records a node restored from discovery storage as exempt from the filter at the endpoint it was bonded at.
    /// </summary>
    /// <remarks>
    /// Only an enode record names the endpoint the node answered a ping from, which is the address admission
    /// exempts. A record that also keeps the signed ENR for address-family fallback is still exempt only at its
    /// bonded endpoint: a node restored at the ENR's endpoint goes through ordinary admission. An ENR-only record
    /// advertises an endpoint the node may never have answered a ping from, and a record written before the bonded
    /// endpoint was stored carries none, so those nodes go through ordinary admission instead of being exempted.
    /// </remarks>
    /// <param name="persistedNode">The persisted record, as loaded from discovery storage.</param>
    public void AddPersistedNode(NetworkNode persistedNode)
    {
        if (_enabled && persistedNode.Enode is { } bondedEndpoint)
        {
            // A legacy row may store an IPv4-mapped host, while a Node address is always unmapped, so compare in that form.
            IPAddress bondedAddress = bondedEndpoint.HostIp;
            _persistedAddresses[persistedNode.NodeId] = bondedAddress.IsIPv4MappedToIPv6
                ? bondedAddress.MapToIPv4()
                : bondedAddress;
        }
    }

    private bool TryAdmitCore(Node node, bool trustInstanceFlags, string? rejectionContext)
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
            _logger.Trace($"Discovery node filtered by recent IP{rejectionContext}: {node:hide}.");
        }

        return false;
    }

    // Accepting and extending happen in one filter lock scope, so a concurrent admission for the same identity
    // cannot evict the entry between the two and reject the node against a reservation it already owns.
    private bool AdmitAddress(Node node) => _filter.TryAcceptOrRefresh(node.Address.Address, node.Id);

    // A bootnode flag comes from configuration, so it cannot go stale. Static and trusted flags can: the operator can
    // remove the node from its manager after the instance was constructed, and live membership, resolved by
    // IsManagedExempt, already covers a node that is still managed. A promotion or refresh re-checks an instance
    // cached minutes earlier, so it ignores every instance flag.
    private bool IsExempt(Node node, bool trustInstanceFlags)
        => _exemptNodeIds.Contains(node.Id)
           || (trustInstanceFlags && node.IsBootnode)
           || IsPersistedAtSameAddress(node);

    private bool IsPersistedAtSameAddress(Node node)
        => _persistedAddresses.TryGetValue(node.Id, out IPAddress? persistedAddress)
           && persistedAddress.Equals(node.Address.Address);

    private bool IsManagedExempt(PublicKey nodeId)
        => _staticNodes?.ContainsNode(nodeId) is true
           || _trustedNodes?.ContainsNode(nodeId) is true;

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
