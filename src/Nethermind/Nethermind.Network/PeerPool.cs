// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Autofac.Features.AttributeFilters;
using Nethermind.Config;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.ServiceStopper;
using Nethermind.Db;
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
        private readonly ITrustedNodesManager _trustedNodesManager;

        public ConcurrentDictionary<PublicKeyAsKey, Peer> ActivePeers { get; } = new();
        public ConcurrentDictionary<PublicKeyAsKey, Peer> Peers { get; } = new();

        public IEnumerable<Peer> NonStaticPeers => Peers.Select(static kvp => kvp.Value).Where(static p => !p.Node.IsStatic);
        public IEnumerable<Peer> StaticPeers => Peers.Select(static kvp => kvp.Value).Where(static p => p.Node.IsStatic);

        public int PeerCount => Peers.Count;
        public int ActivePeerCount => ActivePeers.Count;

        private readonly CancellationTokenSource _cancellationTokenSource = new();

        public PeerPool(
            INodeSource nodeSource,
            INodeStatsManager nodeStatsManager,
            [KeyFilter(DbNames.PeersDb)] INetworkStorage peerStorage,
            INetworkConfig networkConfig,
            ILogManager logManager,
            ITrustedNodesManager trustedNodesManager)

        {
            _nodeSource = nodeSource ?? throw new ArgumentNullException(nameof(nodeSource));
            _stats = nodeStatsManager ?? throw new ArgumentNullException(nameof(nodeStatsManager));
            _peerStorage = peerStorage ?? throw new ArgumentNullException(nameof(peerStorage));
            _networkConfig = networkConfig ?? throw new ArgumentNullException(nameof(networkConfig));
            _peerStorage.StartBatch();
            _logger = logManager?.GetClassLogger<PeerPool>() ?? throw new ArgumentNullException(nameof(logManager));
            _trustedNodesManager = trustedNodesManager ?? throw new ArgumentNullException(nameof(trustedNodesManager));

            _nodeSource.NodeRemoved += NodeSourceOnNodeRemoved;
        }

        private void NodeSourceOnNodeRemoved(object? sender, NodeEventArgs e)
        {
            if (!Peers.TryGetValue(e.Node.Id, out Peer? peer))
                return;

            if (e is not ExplicitNodeRemovalEventArgs)
            {
                // Only remove the peer if no P2P session is active.
                // The dictionary removals are done inside SessionLock so the session check and
                // removal are atomic against AttachSession. PeerRemoved is fired outside the lock
                // to avoid holding it across arbitrary event handler code.
                bool removed;
                lock (peer.SessionLock)
                {
                    removed = peer.InSession is null && peer.OutSession is null && !peer.IsAwaitingConnection
                              && Peers.TryRemove(e.Node.Id, out _);
                }
                if (removed) PeerRemoved?.Invoke(this, new PeerEventArgs(peer));
                return;
            }

            TryRemove(e.Node.Id, out _);
        }

        public Peer GetOrAdd(Node node)
        {
            if (Peers.TryGetValue(node.Id, out Peer? existing)) return existing;

            // ConcurrentDictionary may run the factory on a losing thread; only the thread whose value is
            // actually inserted (reference-equal) fires PeerAdded.
            Peer created = new(node, _stats.GetOrAdd(node));
            Peer peer = Peers.GetOrAdd(node.Id, created);
            if (ReferenceEquals(peer, created))
            {
                if ((node.IsBootnode || node.IsStatic) && _logger.IsDebug) DebugAddingCandidatePeer(node);
                PeerAdded?.Invoke(this, new PeerEventArgs(peer));
            }
            return peer;

            [MethodImpl(MethodImplOptions.NoInlining)]
            void DebugAddingCandidatePeer(Node n)
                => _logger.Debug($"Adding a {(n.IsBootnode ? "bootnode" : "stored")} candidate peer {n:s}");
        }

        public Peer GetOrAdd(NetworkNode networkNode)
        {
            if (Peers.TryGetValue(networkNode.NodeId, out Peer? existing)) return existing;

            Node node = new(networkNode) { IsTrusted = _trustedNodesManager.IsTrusted(networkNode.Enode) };
            Peer created = new(node, _stats.GetOrAdd(node));
            Peer peer = Peers.GetOrAdd(node.Id, created);
            if (ReferenceEquals(peer, created))
            {
                PeerAdded?.Invoke(this, new PeerEventArgs(peer));
            }
            return peer;
        }

        public bool TryGet(PublicKey id, out Peer peer) => Peers.TryGetValue(id, out peer);

        public bool TryRemove(PublicKey id, out Peer peer)
        {
            if (!Peers.TryRemove(id, out peer))
                return false;

            lock (peer.SessionLock)
            {
                peer.InSession?.MarkDisconnected(DisconnectReason.PeerRemoved, DisconnectType.Local, "admin_removePeer");
                peer.OutSession?.MarkDisconnected(DisconnectReason.PeerRemoved, DisconnectType.Local, "admin_removePeer");
                peer.InSession = null;
                peer.OutSession = null;
            }
            PeerRemoved?.Invoke(this, new PeerEventArgs(peer));
            return true;
        }

        public Peer Replace(ISession session)
        {
            if (Peers.TryRemove(session.ObsoleteRemoteNodeId, out Peer previousPeer))
            {
                // this should happen
                if (previousPeer.InSession == session || previousPeer.OutSession == session)
                {
                    // (what with the other session?)
                    previousPeer.InSession = null;
                    previousPeer.OutSession = null;
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
            CancellationToken token = _cancellationTokenSource.Token;
            while (!token.IsCancellationRequested
                && await _peerPersistenceTimer.WaitForNextTickAsync(token))
            {
                try
                {
                    UpdateReputationAndMaxPeersCount();

                    if (!_peerStorage.AnyPendingChange())
                    {
                        if (_logger.IsTrace) _logger.Trace("No changes in peer storage, skipping commit.");
                        continue;
                    }

                    _peerStorage.Commit();
                    _peerStorage.StartBatch();
                }
                catch (Exception ex)
                {
                    _peerStorage.StartBatch();
                    if (_logger.IsError) ErrorPeerStorageCommit(ex);
                }
            }

            [MethodImpl(MethodImplOptions.NoInlining)]
            void ErrorPeerStorageCommit(Exception ex) => _logger.Error($"Error during peer storage commit: {ex}");
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
                Peer[] activePeers = ActivePeers.Select(static kvp => kvp.Value).ToArray();
                CleanupPersistedPeers(activePeers, storedNodes);
            }
        }

        private void CleanupPersistedPeers(ICollection<Peer> activePeers, NetworkNode[] storedNodes)
        {
            HashSet<PublicKey> activeNodeIds = [.. activePeers.Select(x => x.Node.Id)];
            NetworkNode[] nonActiveNodes = storedNodes.Where(x => !activeNodeIds.Contains(x.NodeId))
                .OrderBy(x => x.Reputation).ToArray();
            int countToRemove = storedNodes.Length - _networkConfig.MaxPersistedPeerCount;
            IEnumerable<NetworkNode> nodesToRemove = nonActiveNodes.Take(countToRemove);

            int removedNodes = 0;
            foreach (NetworkNode item in nodesToRemove)
            {
                _peerStorage.RemoveNode(item.NodeId);
                removedNodes++;
            }

            if (_logger.IsDebug) DebugRemovingPersistedPeers(removedNodes, storedNodes.Length);

            [MethodImpl(MethodImplOptions.NoInlining)]
            void DebugRemovingPersistedPeers(int removed, int prevCount)
                => _logger.Debug($"Removing persisted peers: {removed}, prevPersistedCount: {prevCount}, newPersistedCount: {_peerStorage.PersistedNodesCount}, PersistedPeerCountCleanupThreshold: {_networkConfig.PersistedPeerCountCleanupThreshold}, MaxPersistedPeerCount: {_networkConfig.MaxPersistedPeerCount}");
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
            _ = FeedFromNodeSource();
            StartPeerPersistenceTimer();
        }

        private async Task FeedFromNodeSource()
        {
            CancellationToken token = _cancellationTokenSource.Token;

            await foreach (Node node in _nodeSource.DiscoverNodes(token))
            {
                // Static and trusted nodes bypass throttling so they are always registered (static to stay
                // dialable, trusted so inbound connections are recognized and counted even at capacity).
                while (!node.IsStatic &&
                       !node.IsTrusted &&
                       !Peers.ContainsKey(node.Id) &&
                       (PeerCount >= _networkConfig.MaxCandidatePeerCount || ActivePeerCount >= _networkConfig.MaxActivePeers))
                {
                    if (_logger.IsDebug) _logger.Debug("Peer cleanup threshold reached. Throttling discovery.");
                    await Task.Delay(1000, token);
                }

                Peer peer = GetOrAdd(node);
                lock (peer.SessionLock)
                {
                    Node currentNode = peer.Node;
                    bool hasConfiguredEndpoint = currentNode.IsStatic || currentNode.IsTrusted || currentNode.IsBootnode;
                    if (!ReferenceEquals(currentNode, node) &&
                        node.Port > 0 &&
                        !hasConfiguredEndpoint)
                    {
                        currentNode.UpdateEndpoint(node);
                    }

                    currentNode.IsStatic |= node.IsStatic;
                    currentNode.IsTrusted |= node.IsTrusted;
                    currentNode.IsBootnode |= node.IsBootnode;
                }
            }
        }

        public async Task StopAsync()
        {
            _nodeSource.NodeRemoved -= NodeSourceOnNodeRemoved;
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

        string IStoppableService.Description => "peer pool";
    }
}
