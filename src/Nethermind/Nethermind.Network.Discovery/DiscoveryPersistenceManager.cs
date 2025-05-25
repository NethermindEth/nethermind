// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac.Features.AttributeFilters;
using Nethermind.Config;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Network.Discovery.Lifecycle;
using Nethermind.Stats.Model;

namespace Nethermind.Network.Discovery
{
    /// <summary>
    /// Manages persistence operations for the discovery process, including loading nodes from storage
    /// and periodic saving of discovered nodes.
    /// </summary>
    public class DiscoveryPersistenceManager
    {
        private readonly INetworkStorage _discoveryStorage;
        private readonly IDiscoveryManager _discoveryManager;
        private readonly ILogger _logger;
        private readonly int _persistenceInterval;

        /// <summary>
        /// Initializes a new instance of the <see cref="DiscoveryPersistenceManager"/> class.
        /// </summary>
        /// <param name="discoveryStorage">The network storage for persisting discovery nodes.</param>
        /// <param name="nodeStatsManager">Manager for node statistics.</param>
        /// <param name="discv4Adapter">Adapter for Discv4 protocol communication.</param>
        /// <param name="discoveryConfig">Configuration for the discovery process.</param>
        /// <param name="logManager">Log manager for logging events.</param>
        /// <exception cref="ArgumentNullException">Thrown if any required parameter is null.</exception>
        public DiscoveryPersistenceManager(
            [KeyFilter(DbNames.DiscoveryNodes)] INetworkStorage discoveryStorage,
            IDiscoveryManager discoveryManager,
            IDiscoveryConfig discoveryConfig,
            ILogManager logManager)
        {
            _discoveryStorage = discoveryStorage;
            _discoveryManager = discoveryManager;
            _logger = logManager.GetClassLogger();
            _persistenceInterval = discoveryConfig.DiscoveryPersistenceInterval;
        }

        /// <summary>
        /// Loads persisted nodes from storage and pings them to verify their availability.
        /// </summary>
        /// <param name="cancellationToken">Cancellation token to stop the operation.</param>
        /// <returns>A task representing the asynchronous operation.</returns>
        public Task LoadPersistedNodes(CancellationToken cancellationToken)
        {
            NetworkNode[] nodes = _discoveryStorage.GetPersistedNodes();
            foreach (NetworkNode networkNode in nodes)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                if (!_discoveryManager.NodesFilter.Set(networkNode.HostIp))
                {
                    // Already seen this node ip recently
                    continue;
                }

                Node node;
                try
                {
                    node = new Node(networkNode.NodeId, networkNode.Host, networkNode.Port);
                }
                catch (Exception)
                {
                    if (_logger.IsDebug)
                        _logger.Error(
                            $"ERROR/DEBUG peer could not be loaded for {networkNode.NodeId}@{networkNode.Host}:{networkNode.Port}");
                    continue;
                }

                INodeLifecycleManager? manager = _discoveryManager.GetNodeLifecycleManager(node, true);
                if (manager is null)
                {
                    if (_logger.IsDebug)
                    {
                        _logger.Debug(
                            $"Skipping persisted node {networkNode.NodeId}@{networkNode.Host}:{networkNode.Port}, manager couldn't be created");
                    }

                    continue;
                }

                manager.NodeStats.CurrentPersistedNodeReputation = networkNode.Reputation;
                if (_logger.IsTrace)
                    _logger.Trace($"Adding persisted node {networkNode.NodeId}@{networkNode.Host}:{networkNode.Port}");
            }

            if (_logger.IsDebug) _logger.Debug($"Added persisted discovery nodes: {nodes.Length}");

            return Task.CompletedTask;
        }

        /// <summary>
        /// Periodically commits discovered nodes to persistent storage.
        /// </summary>
        /// <param name="cancellationToken">Cancellation token to stop the operation.</param>
        /// <returns>A task representing the asynchronous operation.</returns>
        public async Task RunDiscoveryPersistenceCommit(CancellationToken cancellationToken)
        {
            if (_logger.IsDebug) _logger.Debug("Starting discovery persistence timer");
            PeriodicTimer timer = new PeriodicTimer(TimeSpan.FromMilliseconds(_persistenceInterval));

            while (!cancellationToken.IsCancellationRequested && await timer.WaitForNextTickAsync(cancellationToken))
            {
                try
                {
                    IReadOnlyCollection<INodeLifecycleManager> managers = _discoveryManager.GetNodeLifecycleManagers();
                    DateTime utcNow = DateTime.UtcNow;
                    //we need to update all notes to update reputation
                    _discoveryStorage.UpdateNodes(managers.Select(x => new NetworkNode(x.ManagedNode.Id, x.ManagedNode.Host,
                        x.ManagedNode.Port, x.NodeStats.NewPersistedNodeReputation(utcNow))).ToArray());

                    if (!_discoveryStorage.AnyPendingChange())
                    {
                        if (_logger.IsTrace) _logger.Trace("No changes in discovery storage, skipping commit.");
                        continue;
                    }

                    _discoveryStorage.Commit();
                    _discoveryStorage.StartBatch();
                }
                catch (Exception ex)
                {
                    _logger.Error($"Error during discovery commit: {ex}");
                }
            }
        }
    }
}
