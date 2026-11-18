// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.Net;
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
/// Rows are not pruned before a complete startup load. Afterwards, unproven rows retain priority within the shared
/// 4096-row limit while offline. The first answered request begun after load ends this temporary protection
/// at the next persistence commit. Policy-blocked rows keep their slots; a full blocked store cannot persist new identities.
/// Factory-rejected rows survive startup load only until the first post-load commit if still rejected,
/// independently of online activity.
/// </remarks>
/// <param name="discoveryStorage">The network storage for persisting discovery nodes.</param>
/// <param name="nodeStatsManager">Manager for node statistics.</param>
/// <param name="messageSender">Protocol-specific Kademlia message sender.</param>
/// <param name="kademlia">Kademlia table whose live nodes should be persisted.</param>
/// <param name="discoveryConfig">Configuration for the discovery process.</param>
/// <param name="logManager">Log manager for logging events.</param>
/// <exception cref="ArgumentNullException">Thrown if any required parameter is null.</exception>
public sealed class DiscoveryPersistenceManager(
    INetworkStorage discoveryStorage,
    INodeStatsManager nodeStatsManager,
    IKademliaMessageSender<PublicKey, Node> messageSender,
    IKademlia<PublicKey, Node> kademlia,
    IDiscoveryConfig discoveryConfig,
    ILogManager logManager,
    PeerAddressPolicy addressPolicy)
{

    private readonly INetworkStorage _discoveryStorage = discoveryStorage;
    private readonly INodeStatsManager _nodeStatsManager = nodeStatsManager;
    private readonly IKademliaMessageSender<PublicKey, Node> _messageSender = messageSender;
    private readonly IKademlia<PublicKey, Node> _kademlia = kademlia;
    private readonly ILogger _logger = logManager.GetClassLogger<DiscoveryPersistenceManager>();
    private readonly int _persistenceInterval = discoveryConfig.DiscoveryPersistenceInterval;
    private readonly int _loadConcurrency = Math.Clamp(discoveryConfig.Concurrency, 1, MaximumPersistedNodes);
    private readonly Lock _storageLock = new();
    internal const int MaximumPersistedNodes = 4096;
    private readonly HashSet<PublicKey> _neutralStoredNodes = [];
    private bool _loadCompleted;
    private Func<NetworkNode, PeerAddressPermissions, Node?>? _nodeFactory;
    internal DiscoveryRequestActivity RequestActivity { get; set; } = new();
    internal TimeProvider PersistenceTimeProvider { get; init; } = TimeProvider.System;

    /// <summary>
    /// Loads persisted nodes from storage and pings them to verify their availability.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token to stop the operation.</param>
    /// <param name="nodeFactory">Converts using supplied permissions; All is used only to classify retained rows.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task LoadPersistedNodes(
        CancellationToken cancellationToken,
        Func<NetworkNode, PeerAddressPermissions, Node?>? nodeFactory = null)
    {
        lock (_storageLock)
        {
            _loadCompleted = false;
            RequestActivity.BeginLoad();
            _nodeFactory = nodeFactory;
        }
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
        Node? self = _kademlia.GetAllAtDistance(0).FirstOrDefault();
        NetworkNode[] stored;
        lock (_storageLock)
        {
            stored = _discoveryStorage.GetPersistedNodes();
        }
        List<(NetworkNode Stored, Node Node)> candidates = [with(Math.Min(stored.Length, MaximumPersistedNodes))];
        HashSet<PublicKey> retained = [];
        HashSet<PublicKey> deferred = [];
        for (int index = 0; index < Math.Min(stored.Length, MaximumPersistedNodes); index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            NetworkNode networkNode = stored[index];
            if (networkNode.IsEnode && ((uint)networkNode.Port > IPEndPoint.MaxPort || (uint)networkNode.DiscoveryPort > IPEndPoint.MaxPort))
            {
                _logger.DebugError($"Peer could not be loaded for persisted node {networkNode}: invalid endpoint port.");
                continue;
            }
            Node? node;
            try
            {
                node = nodeFactory is null ? new Node(networkNode) : nodeFactory(networkNode, addressPolicy.AllowedAddresses);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception e)
            {
                _logger.DebugError($"Peer could not be loaded for persisted identity {networkNode.NodeId}. {e}");
                continue;
            }
            if (node is not null && IsAcceptable(node, self))
            {
                if (retained.Add(node.Id)) candidates.Add((networkNode, node));
            }
            else if (nodeFactory is not null && node is null || IsPolicyRejectedStored(networkNode, self))
            {
                if (retained.Add(networkNode.NodeId)) deferred.Add(networkNode.NodeId);
            }
        }

        lock (_storageLock)
        {
            _neutralStoredNodes.Clear();
            _neutralStoredNodes.UnionWith(deferred);
            _discoveryStorage.StartBatch();
            foreach (NetworkNode node in stored)
            {
                if (!retained.Contains(node.NodeId)) _discoveryStorage.RemoveNode(node.NodeId);
            }
            _discoveryStorage.Commit();
        }

        int answered = 0;
        ConcurrentQueue<PublicKey> failed = new();
        ConcurrentQueue<PublicKey> neutral = new();
        try
        {
            await Parallel.ForEachAsync(candidates, new ParallelOptions
            {
                MaxDegreeOfParallelism = _loadConcurrency,
                CancellationToken = cancellationToken
            }, async (candidate, token) =>
            {
                Node node = candidate.Node;
                try
                {
                    if (await _messageSender.Ping(node, token))
                    {
                        Interlocked.Exchange(ref answered, 1);
                        if (_nodeStatsManager.TryGetStats(node, out INodeStats? stats))
                            stats.CurrentPersistedNodeReputation = candidate.Stored.Reputation;
                        DiscoveryLiveness.Mark(node);
                    }
                    else if (!token.IsCancellationRequested) failed.Enqueue(node.Id);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch (KademliaRequestNotSentException)
                {
                    neutral.Enqueue(node.Id);
                }
                catch (Exception e)
                {
                    neutral.Enqueue(node.Id);
                    if (_logger.IsDebug) _logger.Debug($"Error when pinging persisted node {node:s}. {e}");
                }
            });
        }
        finally
        {
            if (!failed.IsEmpty || !neutral.IsEmpty)
            {
                lock (_storageLock)
                {
                    _discoveryStorage.StartBatch();
                    while (neutral.TryDequeue(out PublicKey? neutralId))
                    {
                        if (_neutralStoredNodes.Count < MaximumPersistedNodes) _neutralStoredNodes.Add(neutralId);
                    }
                    HashSet<PublicKey> live = _kademlia.IterateNodes()
                        .Where(node => DiscoveryLiveness.IsRecent(node) && IsAcceptable(node, self))
                        .Take(MaximumPersistedNodes).Select(node => node.Id).ToHashSet();
                    while (failed.TryDequeue(out PublicKey? id))
                    {
                        if (answered == 0)
                        {
                            if (_neutralStoredNodes.Count < MaximumPersistedNodes) _neutralStoredNodes.Add(id);
                        }
                        else
                        {
                            _neutralStoredNodes.Remove(id);
                            if (!live.Contains(id)) _discoveryStorage.RemoveNode(id);
                        }
                    }
                    _discoveryStorage.Commit();
                }
            }
        }
        lock (_storageLock)
        {
            _loadCompleted = true;
            RequestActivity.CompleteLoad();
        }
    }

    internal async Task LoadPersistedNodesInBackground(CancellationToken token, Func<NetworkNode, PeerAddressPermissions, Node?>? nodeFactory = null)
    {
        try
        {
            await LoadPersistedNodes(token, nodeFactory);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _logger.Error("Error loading persisted discovery nodes", exception);
        }
    }

    internal async Task RunDiscoveryPersistenceCommit(Task startupLoad, CancellationToken cancellationToken)
    {
        await startupLoad;
        cancellationToken.ThrowIfCancellationRequested();
        await RunDiscoveryPersistenceCommit(cancellationToken);
    }

    /// <summary>
    /// Periodically commits discovered nodes to persistent storage.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token to stop the operation.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task RunDiscoveryPersistenceCommit(CancellationToken cancellationToken)
    {
        if (_logger.IsDebug) _logger.Debug("Starting discovery persistence timer");
        using PeriodicTimer timer = new(TimeSpan.FromMilliseconds(_persistenceInterval), PersistenceTimeProvider);

        while (!cancellationToken.IsCancellationRequested && await timer.WaitForNextTickAsync(cancellationToken))
        {
            try
            {
                CommitLiveNodes();
            }
            catch (Exception ex)
            {
                _logger.Error($"Error during discovery commit: {ex}");
            }
        }
    }

    internal void CommitLiveNodes()
    {
        Node? self = _kademlia.GetAllAtDistance(0).FirstOrDefault();
        List<NetworkNode> nodes = [];
        HashSet<PublicKey> retained = [];
        foreach (Node node in _kademlia.IterateNodes())
        {
            if (nodes.Count == MaximumPersistedNodes) break;
            if (node.LastDiscoveryProof is not { } proof || !DiscoveryLiveness.IsRecent(node)
                || !IsAcceptable(node, self) || !retained.Add(node.Id)) continue;
            nodes.Add(CreatePersistedNode(node, _nodeStatsManager.GetNewPersistedReputation(node), proof));
        }

        lock (_storageLock)
        {
            NetworkNode[] storedNodes = _discoveryStorage.GetPersistedNodes();
            HashSet<PublicKey> storedIds = [];
            for (int i = 0; i < Math.Min(storedNodes.Length, MaximumPersistedNodes); i++) storedIds.Add(storedNodes[i].NodeId);
            if (_loadCompleted)
            {
                if (RequestActivity.IsOnline) _neutralStoredNodes.Clear();
                _neutralStoredNodes.IntersectWith(storedIds);
                foreach (NetworkNode stored in storedNodes)
                {
                    if (IsPolicyRejectedStored(stored, self))
                    {
                        if (_neutralStoredNodes.Count < MaximumPersistedNodes) _neutralStoredNodes.Add(stored.NodeId);
                    }
                    else if (!IsAcceptableStored(stored, self)) _neutralStoredNodes.Remove(stored.NodeId);
                }
                _neutralStoredNodes.ExceptWith(retained);
                int available = MaximumPersistedNodes - _neutralStoredNodes.Count;
                if (nodes.Count > available) nodes.RemoveRange(available, nodes.Count - available);
                retained.Clear();
                foreach (NetworkNode node in nodes) retained.Add(node.NodeId);
                retained.UnionWith(_neutralStoredNodes);
            }
            else
            {
                int available = Math.Max(0, MaximumPersistedNodes - storedNodes.Length);
                List<NetworkNode> updates = [with(nodes.Count)];
                foreach (NetworkNode node in nodes)
                {
                    if (!storedIds.Contains(node.NodeId))
                    {
                        if (available == 0) continue;
                        available--;
                    }
                    updates.Add(node);
                }
                nodes = updates;
            }
            _discoveryStorage.StartBatch();
            if (_loadCompleted)
            {
                foreach (NetworkNode stored in storedNodes)
                {
                    if (!retained.Contains(stored.NodeId)) _discoveryStorage.RemoveNode(stored.NodeId);
                }
            }
            _discoveryStorage.UpdateNodes(nodes);
            _discoveryStorage.Commit();
        }
    }

    private bool IsAcceptable(Node node, Node? self)
        => IsAcceptable(node, self, addressPolicy.AllowedAddresses);

    private static bool IsAcceptable(Node node, Node? self, PeerAddressPermissions permissions)
        => node.HasDiscoveryEndpoint && (self is null || (!node.Id.Equals(self.Id)
            && !PeerAddressPolicy.IsSelfEndpoint(node.DiscoveryAddress, self.DiscoveryAddress.Address, self.DiscoveryPort)))
            && PeerAddressPolicy.IsAcceptable(node.DiscoveryAddress.Address,
                permissions);

    private bool IsAcceptableStored(NetworkNode stored, Node? self)
    {
        Node? node = RestoreStored(stored, addressPolicy.AllowedAddresses);
        return node is not null && IsAcceptable(node, self);
    }

    private bool IsPolicyRejectedStored(NetworkNode stored, Node? self)
    {
        Node? node = RestoreStored(stored, PeerAddressPermissions.All);
        return node is not null && IsAcceptable(node, self, PeerAddressPermissions.All)
            && !PeerAddressPolicy.IsAcceptable(node.DiscoveryAddress.Address, addressPolicy.AllowedAddresses)
            && !IsAcceptableStored(stored, self);
    }

    private Node? RestoreStored(NetworkNode stored, PeerAddressPermissions permissions)
    {
        if (_nodeFactory is { } factory) return factory(stored, permissions);
        try { return new Node(stored); }
        catch (Exception exception)
        {
            _logger.DebugError($"Peer could not be loaded for persisted node {stored}. {exception}");
            return null;
        }
    }

    private static NetworkNode CreatePersistedNode(Node node, long reputation, Node.DiscoveryProof proof)
    {
        IPEndPoint endpoint = new(proof.Address, proof.Port);
        if (node.Enr is { } record && node.IsVerifiedEnr(record)
            && CompositeDiscoveryApp.TryCreateReachableDiscoveryNode(record, proof.Address, endpoint, out Node? recorded, allowNonRoutable: PeerAddressPermissions.All)
            && recorded.DiscoveryAddress.Equals(endpoint))
        {
            return new NetworkNode(node.Enr.ToString()) { Reputation = reputation };
        }

        return new NetworkNode(new Enode(node.Id, proof.Address, node.Port, proof.Port)) { Reputation = reputation };
    }
}
