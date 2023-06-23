// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Config;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Logging;
using Nethermind.Network.Config;
using Nethermind.Network.P2P;
using Nethermind.Stats;
using Nethermind.Stats.Model;

namespace Nethermind.Network
{
    public class PeerPool : IPeerPool
    {
        private PeriodicTimer _peerPersistenceTimer;
        private Task? _storageCommitTask;

        private readonly INodeSource _nodeSource;
        private readonly INodeStatsManager _stats;
        private readonly INetworkStorage _peerStorage;
        private readonly INetworkConfig _networkConfig;
        private readonly ILogger _logger;

        public ConcurrentDictionary<PublicKey, Peer> ActivePeers { get; } = new();
        public ConcurrentDictionary<PublicKey, Peer> Peers { get; } = new();
        private readonly ConcurrentDictionary<PublicKey, Peer> _staticPeers = new();

        public IEnumerable<Peer> NonStaticPeers => Peers.Values.Where(p => !p.Node.IsStatic);
        public IEnumerable<Peer> StaticPeers => _staticPeers.Values;

        public int PeerCount => Peers.Count;
        public int ActivePeerCount => ActivePeers.Count;
        public int StaticPeerCount => _staticPeers.Count;

        private readonly CancellationTokenSource _cancellationTokenSource = new();

        Func<PublicKey, (Node Node, ConcurrentDictionary<PublicKey, Peer> Statics), Peer> _createNewNodePeer;
        Func<PublicKey, (NetworkNode Node, ConcurrentDictionary<PublicKey, Peer> Statics), Peer> _createNewNetworkNodePeer;

        public PeerPool(
            INodeSource nodeSource,
            INodeStatsManager nodeStatsManager,
            INetworkStorage peerStorage,
            INetworkConfig networkConfig,
            ILogManager logManager)
        {
            _nodeSource = nodeSource ?? throw new ArgumentNullException(nameof(nodeSource));
            _stats = nodeStatsManager ?? throw new ArgumentNullException(nameof(nodeStatsManager));
            _peerStorage = peerStorage ?? throw new ArgumentNullException(nameof(peerStorage));
            _networkConfig = networkConfig ?? throw new ArgumentNullException(nameof(networkConfig));
            _peerStorage.StartBatch();
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));

            // Early explicit closure
            _createNewNodePeer = CreateNew;
            _createNewNetworkNodePeer = CreateNew;

            _nodeSource.NodeAdded += NodeSourceOnNodeAdded;
            _nodeSource.NodeRemoved += NodeSourceOnNodeRemoved;
        }

        private void NodeSourceOnNodeRemoved(object? sender, NodeEventArgs e)
        {
            TryRemove(e.Node.Id, out _);
        }

        private void NodeSourceOnNodeAdded(object? sender, NodeEventArgs e)
        {
            // _logger.Error($"Adding a node from source {sender}: {e.Node}");
            GetOrAdd(e.Node);
        }

        public Peer GetOrAdd(Node node)
        {
            return Peers.GetOrAdd(node.Id, valueFactory: _createNewNodePeer, (node, _staticPeers));
        }

        public Peer GetOrAdd(NetworkNode node)
        {
            return Peers.GetOrAdd(node.NodeId, valueFactory: _createNewNetworkNodePeer, (node, _staticPeers));
        }

        private Peer CreateNew(PublicKey key, (Node Node, ConcurrentDictionary<PublicKey, Peer> Statics) arg)
        {
            if (arg.Node.IsBootnode || arg.Node.IsStatic)
            {
                if (_logger.IsDebug) _logger.Debug(
                    $"Adding a {(arg.Node.IsBootnode ? "bootnode" : "stored")} candidate peer {arg.Node:s}");
            }
            Peer peer = new(arg.Node, _stats.GetOrAdd(arg.Node));
            if (arg.Node.IsStatic)
            {
                arg.Statics.TryAdd(arg.Node.Id, peer);
            }

            PeerAdded?.Invoke(this, new PeerEventArgs(peer));
            return peer;
        }

        private Peer CreateNew(PublicKey key, (NetworkNode Node, ConcurrentDictionary<PublicKey, Peer> Statics) arg)
        {
            Node node = new(arg.Node);
            Peer peer = new(node, _stats.GetOrAdd(node));

            PeerAdded?.Invoke(this, new PeerEventArgs(peer));
            return peer;
        }

        public bool TryGet(PublicKey id, out Peer peer)
        {
            return Peers.TryGetValue(id, out peer);
        }

        public bool TryRemove(PublicKey id, out Peer peer)
        {
            if (Peers.TryRemove(id, out peer))
            {
                _staticPeers.TryRemove(id, out _);
                peer.InSession?.MarkDisconnected(EthDisconnectReason.DisconnectRequested, DisconnectType.Local, "admin_removePeer");
                peer.OutSession?.MarkDisconnected(EthDisconnectReason.DisconnectRequested, DisconnectType.Local, "admin_removePeer");
                peer.InSession = null;
                peer.OutSession = null;
                PeerRemoved?.Invoke(this, new PeerEventArgs(peer));
                return true;
            }

            return false;
        }

        public Peer Replace(ISession session)
        {
            if (Peers.TryRemove(session.ObsoleteRemoteNodeId, out Peer previousPeer))
            {
                // this should happen
                if (previousPeer.InSession == session || previousPeer.OutSession == session)
                {
                    if (previousPeer.Node.IsStatic)
                    {
                        session.Node.IsStatic = true;
                    }

                    // (what with the other session?)

                    _staticPeers.TryRemove(session.ObsoleteRemoteNodeId, out _);

                    if (previousPeer is not null)
                    {
                        previousPeer.InSession = null;
                        previousPeer.OutSession = null;
                    }
                }
                else
                {
                    _logger.Error("Trying to update node ID on an unknown peer - requires investigation, please report the issue.");
                }
            }

            Peer newPeer = GetOrAdd(session.Node);
            newPeer.InSession = session.Direction == ConnectionDirection.In ? session : null;
            newPeer.OutSession = session.Direction == ConnectionDirection.Out ? session : null;
            return newPeer;
        }

        public event EventHandler<PeerEventArgs>? PeerAdded;
        public event EventHandler<PeerEventArgs>? PeerRemoved;

        private void StartPeerPersistenceTimer()
        {
            if (_logger.IsDebug) _logger.Debug("Starting peer persistence timer");

            _peerPersistenceTimer = new PeriodicTimer(TimeSpan.FromMilliseconds(_networkConfig.PeersPersistenceInterval));

            _storageCommitTask = RunPeerCommit();
        }

        private async Task RunPeerCommit()
        {
            var token = _cancellationTokenSource.Token;
            while (!token.IsCancellationRequested
                && await _peerPersistenceTimer.WaitForNextTickAsync(token))
            {
                try
                {
                    UpdateReputationAndMaxPeersCount();

                    if (!_peerStorage.AnyPendingChange())
                    {
                        if (_logger.IsTrace) _logger.Trace("No changes in peer storage, skipping commit.");
                        return;
                    }

                    _peerStorage.Commit();
                    _peerStorage.StartBatch();
                }
                catch (Exception ex)
                {
                    _logger.Error($"Error during peer storage commit: {ex}");
                }
            }
        }

        private void UpdateReputationAndMaxPeersCount()
        {
            NetworkNode[] storedNodes = _peerStorage.GetPersistedNodes();
            foreach (NetworkNode networkNode in storedNodes)
            {
                if (networkNode.Port < 0 || networkNode.Port > ushort.MaxValue)
                {
                    continue;
                }

                Peer peer = GetOrAdd(networkNode);
                long newRep = _stats.GetNewPersistedReputation(peer.Node);
                if (newRep != networkNode.Reputation)
                {
                    networkNode.Reputation = newRep;
                    _peerStorage.UpdateNode(networkNode);
                }
            }

            //if we have more persisted nodes then the threshold, we run cleanup process
            if (storedNodes.Length > _networkConfig.PersistedPeerCountCleanupThreshold)
            {
                ICollection<Peer> activePeers = ActivePeers.Values;
                CleanupPersistedPeers(activePeers, storedNodes);
            }
        }

        private void CleanupPersistedPeers(ICollection<Peer> activePeers, NetworkNode[] storedNodes)
        {
            HashSet<PublicKey> activeNodeIds = new(activePeers.Select(x => x.Node.Id));
            NetworkNode[] nonActiveNodes = storedNodes.Where(x => !activeNodeIds.Contains(x.NodeId))
                .OrderBy(x => x.Reputation).ToArray();
            int countToRemove = storedNodes.Length - _networkConfig.MaxPersistedPeerCount;
            var nodesToRemove = nonActiveNodes.Take(countToRemove);

            int removedNodes = 0;
            foreach (var item in nodesToRemove)
            {
                _peerStorage.RemoveNode(item.NodeId);
                removedNodes++;
            }

            if (_logger.IsDebug) _logger.Debug($"Removing persisted peers: {removedNodes}, prevPersistedCount: {storedNodes.Length}, newPersistedCount: {_peerStorage.PersistedNodesCount}, PersistedPeerCountCleanupThreshold: {_networkConfig.PersistedPeerCountCleanupThreshold}, MaxPersistedPeerCount: {_networkConfig.MaxPersistedPeerCount}");
        }

        private void StopTimers()
        {
            try
            {
                if (_logger.IsDebug) _logger.Debug("Stopping peer timers");

                _peerPersistenceTimer?.Dispose();
            }
            catch (Exception e)
            {
                _logger.Error("Error during peer timers stop", e);
            }
        }

        public void Start()
        {
            List<Node> initialNodes = _nodeSource.LoadInitialList();
            foreach (Node initialNode in initialNodes)
            {
                GetOrAdd(initialNode);
            }

            StartPeerPersistenceTimer();
        }

        public async Task StopAsync()
        {
            _cancellationTokenSource.Cancel();

            StopTimers();

            Task storageCloseTask = Task.CompletedTask;
            if (_storageCommitTask is not null)
            {
                storageCloseTask = _storageCommitTask.ContinueWith(x =>
                {
                    if (x.IsFailedButNotCanceled())
                    {
                        if (_logger.IsError) _logger.Error("Error during peer persistence stop.", x.Exception);
                    }
                });
            }

            await storageCloseTask;
            if (_logger.IsInfo) _logger.Info("Peer Pool shutdown complete.. please wait for all components to close");
        }
    }
}
