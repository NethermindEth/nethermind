// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;
using Nethermind.Core.Crypto;
using Nethermind.Kademlia;
using Nethermind.Logging;
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
/// that was admitted, and an ENR may advertise an endpoint that never answered a ping. discv5 has no such
/// exemption, so it keeps the ENR and lets restore choose a reachable family.
/// </para>
/// </remarks>
/// <param name="discoveryStorage">The network storage for persisting discovery nodes.</param>
/// <param name="nodeStatsManager">Manager for node statistics.</param>
/// <param name="messageSender">Protocol-specific Kademlia message sender.</param>
/// <param name="kademlia">Kademlia table whose live nodes should be persisted.</param>
/// <param name="discoveryConfig">Configuration for the discovery process.</param>
/// <param name="logManager">Log manager for logging events.</param>
/// <param name="persistBondedEndpoint">
/// Whether to persist the bonded routing endpoint instead of the node's ENR.
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
    /// <param name="onNodeRestored">Optional callback invoked after a node is restored successfully and before it is pinged.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task LoadPersistedNodes(
        CancellationToken cancellationToken,
        Func<NetworkNode, Node?>? nodeFactory = null,
        Action<Node>? onNodeRestored = null)
    {
        NetworkNode[] nodes = _discoveryStorage.GetPersistedNodes();
        foreach (NetworkNode networkNode in nodes)
        {
            if (cancellationToken.IsCancellationRequested) break;

            Node? node;
            try
            {
                node = nodeFactory is null ? new Node(networkNode) : nodeFactory(networkNode);
                if (node is null || !node.HasDiscoveryEndpoint)
                {
                    continue;
                }
            }
            catch (Exception e)
            {
                _logger.DebugError($"Peer could not be loaded for persisted node {networkNode:hide}. {e:hide}");

                continue;
            }

            onNodeRestored?.Invoke(node);

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

        return new NetworkNode(new Enode(node.Id, node.Address.Address, node.Port, node.DiscoveryPort)) { Reputation = reputation };
    }
}
