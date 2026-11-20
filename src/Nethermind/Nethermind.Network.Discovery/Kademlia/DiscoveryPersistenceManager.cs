// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net.Sockets;
using Nethermind.Config;
using Nethermind.Core.Crypto;
using Nethermind.Kademlia;
using Nethermind.Logging;
using Nethermind.Network.Enr;
using Nethermind.Stats;
using Nethermind.Stats.Model;

namespace Nethermind.Network.Discovery.Kademlia;

/// <summary>
/// Manages persistence operations for the discovery process, including loading nodes from storage
/// and periodic saving of discovered nodes.
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="DiscoveryPersistenceManager"/> class.
/// <para>
/// discv4 persists the bonded endpoint because its persisted-node admission exemption is scoped to the address
/// that was admitted, and an ENR may advertise an endpoint that never answered a ping. When the verified signed ENR
/// advertises the other address family, it is persisted alongside the endpoint so a restore can still reach the
/// node after a listener-family change. discv5 has no such exemption, so it keeps the ENR and lets restore choose a
/// reachable family.
/// </para>
/// </remarks>
/// <param name="discoveryStorage">The network storage for persisting discovery nodes.</param>
/// <param name="nodeStatsManager">Manager for node statistics.</param>
/// <param name="messageSender">Protocol-specific Kademlia message sender.</param>
/// <param name="kademlia">Kademlia table whose live nodes should be persisted.</param>
/// <param name="discoveryConfig">Configuration for the discovery process.</param>
/// <param name="logManager">Log manager for logging events.</param>
/// <param name="persistBondedEndpoint">
/// Whether to persist the bonded routing endpoint instead of the node's ENR. When enabled, a verified signed ENR
/// is stored alongside the endpoint when it offers an endpoint in the other address family.
/// </param>
/// <exception cref="ArgumentNullException">Thrown if any required parameter is null.</exception>
public sealed class DiscoveryPersistenceManager(
    INetworkStorage discoveryStorage,
    INodeStatsManager nodeStatsManager,
    IKademliaMessageSender<PublicKey, Node> messageSender,
    IKademlia<PublicKey, Node> kademlia,
    IDiscoveryConfig discoveryConfig,
    ILogManager logManager,
    bool persistBondedEndpoint = false)
{
    private readonly INetworkStorage _discoveryStorage = discoveryStorage;
    private readonly INodeStatsManager _nodeStatsManager = nodeStatsManager;
    private readonly IKademliaMessageSender<PublicKey, Node> _messageSender = messageSender;
    private readonly IKademlia<PublicKey, Node> _kademlia = kademlia;
    private readonly bool _persistBondedEndpoint = persistBondedEndpoint;
    private readonly ILogger _logger = logManager.GetClassLogger<DiscoveryPersistenceManager>();
    private readonly int _persistenceInterval = discoveryConfig.DiscoveryPersistenceInterval;

    /// <summary>
    /// Loads persisted nodes from storage and pings them to verify their availability.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token to stop the operation.</param>
    /// <param name="nodeFactory">Optional protocol-specific conversion for persisted nodes.</param>
    /// <param name="onNodeRestored">Optional callback invoked with the restored node and the persisted record it came from, after a node is restored successfully and before it is pinged.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task LoadPersistedNodes(
        CancellationToken cancellationToken,
        Func<NetworkNode, Node?>? nodeFactory = null,
        Action<Node, NetworkNode>? onNodeRestored = null)
    {
        NetworkNode[] nodes = _discoveryStorage.GetPersistedNodes();
        foreach (NetworkNode networkNode in nodes)
        {
            if (cancellationToken.IsCancellationRequested) break;

            Node? node;
            try
            {
                node = nodeFactory is null ? new Node(networkNode) : nodeFactory(networkNode);
                if (node is null)
                {
                    if (_logger.IsTrace) _logger.Trace($"Persisted node {networkNode:hide} has no endpoint the local listener can reach; skipping it.");
                    continue;
                }

                if (!node.HasDiscoveryEndpoint)
                {
                    continue;
                }
            }
            catch (Exception e)
            {
                _logger.DebugError($"Peer could not be loaded for persisted node {networkNode:hide}. {e:hide}");

                continue;
            }

            onNodeRestored?.Invoke(node, networkNode);

            try
            {
                // Reputation must be set before Ping so the routing table has the correct reputation when the Pong is received.
                _nodeStatsManager.GetOrAdd(node).CurrentPersistedNodeReputation = networkNode.Reputation;
                if (!await _messageSender.Ping(node, cancellationToken))
                {
                    continue;
                }
            }
            catch (OperationCanceledException)
            {
                // Ping returns false on timeout; an OCE here means lifecycle cancellation, so stop promptly.
                throw;
            }
            catch (Exception e)
            {
                if (_logger.IsDebug) _logger.Debug($"Error when pinging persisted node {networkNode.NodeId:hide}@{networkNode.Host:hide}:{networkNode.Port:hide}. {e:hide}");

                continue;
            }

            if (_logger.IsTrace)
                _logger.Trace($"Adding persisted node {networkNode.NodeId:hide}@{networkNode.Host:hide}:{networkNode.Port:hide}");
        }

        if (_logger.IsDebug) _logger.Debug($"Added persisted discovery nodes: {nodes.Length}");
    }

    /// <summary>
    /// Periodically commits discovered nodes to persistent storage.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token to stop the operation.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task RunDiscoveryPersistenceCommit(CancellationToken cancellationToken)
    {
        if (_logger.IsDebug) _logger.Debug("Starting discovery persistence timer");
        using PeriodicTimer timer = new(TimeSpan.FromMilliseconds(_persistenceInterval));

        while (!cancellationToken.IsCancellationRequested && await timer.WaitForNextTickAsync(cancellationToken))
        {
            try
            {
                List<NetworkNode> nodes = [];
                foreach (Node node in _kademlia.IterateNodes())
                {
                    long reputation = _nodeStatsManager.GetNewPersistedReputation(node);
                    nodes.Add(CreatePersistedNode(node, reputation));
                }

                _discoveryStorage.StartBatch();
                _discoveryStorage.UpdateNodes(nodes);
                _discoveryStorage.Commit();
            }
            catch (Exception ex)
            {
                _logger.Error($"Error during discovery commit: {ex}");
            }
        }
    }

    private NetworkNode CreatePersistedNode(Node node, long reputation)
    {
        if (!_persistBondedEndpoint && node.Enr is { } enr)
        {
            return new NetworkNode(enr.ToString()) { Reputation = reputation };
        }

        return new NetworkNode(new Enode(node.Id, node.Address.Address, node.Port, node.DiscoveryPort))
        {
            Reputation = reputation,
            FallbackEnr = GetFallbackEnr(node)
        };
    }

    /// <summary>
    /// Returns the verified signed ENR when it advertises a discovery endpoint in the family the bonded endpoint
    /// does not use, which is the only case a listener change can make it the reachable choice.
    /// </summary>
    /// <remarks>
    /// Storing the record unconditionally would move every discv4 row to the node-string format, several times
    /// larger, for an endpoint restore can never prefer.
    /// </remarks>
    private static NodeRecord? GetFallbackEnr(Node node)
    {
        if (node.Enr is not { Signature: not null } enr || !node.IsVerifiedEnr(enr))
        {
            return null;
        }

        if (!node.HasDiscoveryEndpoint)
        {
            return enr;
        }

        AddressFamily alternateFamily = DiscoveryAddressSupport.GetFamily(node.Address.Address) == AddressFamily.InterNetwork
            ? AddressFamily.InterNetworkV6
            : AddressFamily.InterNetwork;

        return enr.TryGetDiscoveryEndpoint(alternateFamily, out _) ? enr : null;
    }
}
