// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Net;
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
        private Task? _feedTask;

        private readonly INodeSource _nodeSource;
        private readonly INodeStatsManager _stats;
        private readonly INetworkStorage _peerStorage;
        private readonly INetworkConfig _networkConfig;
        private readonly ILogger _logger;
        private readonly ITrustedNodesManager _trustedNodesManager;
        private readonly TimeProvider _timeProvider;
        private readonly PeerAddressPolicy _addressPolicy;
        private readonly Lock _persistenceLock = new();

        public ConcurrentDictionary<PublicKeyAsKey, Peer> ActivePeers { get; } = new();
        public ConcurrentDictionary<PublicKeyAsKey, Peer> Peers { get; } = new();

        public IEnumerable<Peer> NonStaticPeers => Peers.Select(static kvp => kvp.Value).Where(static p => !p.Node.IsStatic);
        public IEnumerable<Peer> StaticPeers => Peers.Select(static kvp => kvp.Value).Where(static p => p.Node.IsStatic);

        public int PeerCount => Peers.Count;
        public int ActivePeerCount => ActivePeers.Count;

        private readonly CancellationTokenSource _cancellationTokenSource = new();

        /// <summary>Creates a pool using the shared peer address policy and supplied clock.</summary>
        public PeerPool(
            INodeSource nodeSource,
            INodeStatsManager nodeStatsManager,
            [KeyFilter(DbNames.PeersDb)] INetworkStorage peerStorage,
            INetworkConfig networkConfig,
            ILogManager logManager,
            ITrustedNodesManager trustedNodesManager,
            TimeProvider timeProvider,
            PeerAddressPolicy addressPolicy)
        {
            ArgumentNullException.ThrowIfNull(addressPolicy);
            _addressPolicy = addressPolicy;
            _nodeSource = nodeSource ?? throw new ArgumentNullException(nameof(nodeSource));
            _stats = nodeStatsManager ?? throw new ArgumentNullException(nameof(nodeStatsManager));
            _peerStorage = peerStorage ?? throw new ArgumentNullException(nameof(peerStorage));
            _networkConfig = networkConfig ?? throw new ArgumentNullException(nameof(networkConfig));
            _peerStorage.StartBatch();
            _logger = logManager?.GetClassLogger<PeerPool>() ?? throw new ArgumentNullException(nameof(logManager));
            _trustedNodesManager = trustedNodesManager ?? throw new ArgumentNullException(nameof(trustedNodesManager));
            _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

            _nodeSource.NodeRemoved += NodeSourceOnNodeRemoved;
        }

        private void NodeSourceOnNodeRemoved(object? sender, NodeEventArgs e)
        {
            if (e is ExplicitNodeRemovalEventArgs)
            {
                TryRemove(e.Node.Id, out _);
                return;
            }

            if (!Peers.TryGetValue(e.Node.Id, out Peer? peer) || peer.Node.IsStatic || peer.Node.IsTrusted)
                return;

            // Only remove the peer if no P2P session is active.
            // The dictionary removals are done inside SessionLock so the session check and
            // removal are atomic against AttachSession. PeerRemoved is fired outside the lock
            // to avoid holding it across arbitrary event handler code.
            TryRemoveInactive(peer, ordinaryCandidateOnly: false);
        }

        public Peer GetOrAdd(Node node) => TryGetOrAdd(node, out Peer? peer)
            ? peer : CreateSessionOnlyPeer(node);

        /// <inheritdoc/>
        public bool TryGetOrAdd(Node node, [NotNullWhen(true)] out Peer? peer, bool inboundCandidate = false)
        {
            if (Peers.TryGetValue(node.Id, out peer))
            {
                MergeNodeState(node, peer);
                return true;
            }

            bool privileged = node.IsStatic || node.IsTrusted;
            if (!_stats.TryAcquire(node, privileged, out INodeStats? stats, out IDisposable? lease))
            {
                if (privileged || inboundCandidate || !TryReclaimCandidate() || !_stats.TryAcquire(node, false, out stats, out lease))
                    return Peers.TryGetValue(node.Id, out peer);
            }
            Peer created = new(node, stats, lease, _stats) { IsInboundCandidate = inboundCandidate };
            peer = Peers.GetOrAdd(node.Id, created);
            if (ReferenceEquals(peer, created))
            {
                if ((node.IsBootnode || node.IsStatic) && _logger.IsTrace) TraceAddingCandidatePeer(node);
                PeerAdded?.Invoke(this, new PeerEventArgs(peer));
            }
            else
            {
                created.ReleaseStats();
                MergeNodeState(node, peer);
            }
            return true;

            [MethodImpl(MethodImplOptions.NoInlining)]
            void TraceAddingCandidatePeer(Node n)
                => _logger.Trace($"Adding a {(n.IsBootnode ? "bootnode" : "stored")} candidate peer {n.ToString("s"):hide}");
        }

        private bool TryReclaimCandidate()
        {
            foreach (KeyValuePair<PublicKeyAsKey, Peer> entry in Peers)
                if (TryRemoveInactive(entry.Value, ordinaryCandidateOnly: true)) return true;
            return false;
        }

        internal bool TryRemoveInactive(Peer candidate, bool ordinaryCandidateOnly)
        {
            lock (candidate.SessionLock)
            {
                if (ordinaryCandidateOnly && (candidate.Node.IsStatic || candidate.Node.IsTrusted ||
                    candidate.Node.IsBootnode || ActivePeers.ContainsKey(candidate.Node.Id))) return false;
                if (candidate.InSession is not null || candidate.OutSession is not null || candidate.IsAwaitingConnection ||
                    !TryRemoveEntry(candidate.Node.Id, out _, candidate)) return false;
            }
            candidate.ReleaseStats();
            PeerRemoved?.Invoke(this, new PeerEventArgs(candidate));
            return true;
        }

        private bool TryRemoveEntry(PublicKey id, out Peer peer, Peer? expected = null)
        {
            while (Peers.TryGetValue(id, out peer))
            {
                if (expected is not null && !ReferenceEquals(expected, peer)) return false;
                lock (peer.SessionLock)
                {
                    if (peer.RemovedFromPool || !Peers.TryRemove(id, out _)) continue;
                    peer.RemovedFromPool = true;
                    return true;
                }
            }
            return false;
        }

        private void MergeNodeState(Node incoming, Peer pooled)
        {
            PromoteFlags(incoming, pooled.Node);
            ReservePriorityStats(pooled);
            incoming.MergeEnrStateFrom(pooled.Node);
        }

        private static void ReservePriorityStats(Peer peer)
        {
            if (peer.Node.IsStatic || peer.Node.IsTrusted) peer.PromoteStats();
        }

        // A node id can reach the pool through several sources (the persisted peers db, discovery, the
        // static/trusted config) and the first arrival wins the dictionary slot. Elevated flags from a later
        // arrival must land on the pooled instance, otherwise a static peer that is also in the persisted
        // peers db loses its static status for the whole session and is never redialed as one.
        private void PromoteFlags(Node incoming, Node pooled)
        {
            if (incoming.IsStatic && !pooled.IsStatic)
            {
                pooled.IsStatic = true;
                if (_logger.IsDebug) DebugPromoted(pooled, "static");
            }

            if (incoming.IsTrusted && !pooled.IsTrusted)
            {
                pooled.IsTrusted = true;
                if (_logger.IsDebug) DebugPromoted(pooled, "trusted");
            }

            if (incoming.IsBootnode && !pooled.IsBootnode)
            {
                pooled.IsBootnode = true;
                if (_logger.IsDebug) DebugPromoted(pooled, "bootnode");
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void DebugPromoted(Node node, string role)
            => _logger.Debug($"Promoting already pooled peer {node.ToString("s"):hide} to {role}");

        bool IPeerPool.TryReserveStats(Node node, out IDisposable? lease)
            => _stats.TryAcquire(node, true, out _, out lease);

        public Peer GetOrAdd(NetworkNode networkNode) => TryGetOrAdd(networkNode, out Peer? peer)
            ? peer : CreateSessionOnlyPeer(new Node(networkNode)
            {
                IsTrusted = networkNode.IsEnode && _trustedNodesManager.IsTrusted(networkNode.Enode)
            });

        private Peer CreateSessionOnlyPeer(Node node) => new(node, _stats.GetStatsOrNull(node) ?? new NodeStatsLight(node));

        internal bool TryGetOrAdd(NetworkNode networkNode, [NotNullWhen(true)] out Peer? peer)
        {
            bool isTrusted = networkNode.IsEnode && _trustedNodesManager.IsTrusted(networkNode.Enode);
            if (Peers.TryGetValue(networkNode.NodeId, out Peer? existing) &&
                (networkNode.IsEnode ||
                 (existing.Node.Enr is { } current && existing.Node.IsVerifiedEnr(current) &&
                  current.EnrSequence >= networkNode.Enr!.EnrSequence)))
            {
                if (isTrusted && !existing.Node.IsTrusted)
                {
                    existing.Node.IsTrusted = true;
                    ReservePriorityStats(existing);
                    if (_logger.IsDebug) DebugPromoted(existing.Node, "trusted");
                }

                peer = existing;
                return true;
            }

            Node node = new(networkNode)
            {
                IsTrusted = isTrusted
            };
            return TryGetOrAdd(node, out peer);
        }

        public bool TryGet(PublicKey id, out Peer peer) => Peers.TryGetValue(id, out peer);

        public bool TryRemove(PublicKey id, out Peer peer)
        {
            lock (_persistenceLock)
            {
                _peerStorage.RemoveNode(id);
                if (!TryRemoveEntry(id, out peer)) return false;
            }

            lock (peer.SessionLock)
            {
                peer.InSession?.MarkDisconnected(DisconnectReason.PeerRemoved, DisconnectType.Local, "admin_removePeer");
                peer.OutSession?.MarkDisconnected(DisconnectReason.PeerRemoved, DisconnectType.Local, "admin_removePeer");
                peer.InSession = null;
                peer.OutSession = null;
            }
            peer.ReleaseStats();
            PeerRemoved?.Invoke(this, new PeerEventArgs(peer));
            return true;
        }

        public Peer Replace(ISession session)
        {
            if (TryRemoveEntry(session.ObsoleteRemoteNodeId, out Peer previousPeer))
            {
                previousPeer.ReleaseStats();
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

            if (!TryGetOrAdd(session.Node, out Peer? newPeer))
                newPeer = new Peer(session.Node, _stats.GetStatsOrNull(session.Node) ?? new NodeStatsLight(session.Node));
            newPeer.InSession = session.Direction == ConnectionDirection.In ? session : null;
            newPeer.OutSession = session.Direction == ConnectionDirection.Out ? session : null;
            return newPeer;
        }

        public event EventHandler<PeerEventArgs>? PeerAdded;
        public event EventHandler<PeerEventArgs>? PeerRemoved;

        private void StartPeerPersistenceTimer()
        {
            if (_logger.IsDebug) _logger.Debug("Starting peer persistence timer");

            _peerPersistenceTimer = new PeriodicTimer(TimeSpan.FromMilliseconds(_networkConfig.PeersPersistenceInterval), _timeProvider);

            _storageCommitTask = RunPeerCommit();
        }

        private async Task RunPeerCommit()
        {
            CancellationToken token = _cancellationTokenSource.Token;
            while (!token.IsCancellationRequested
                && await _peerPersistenceTimer.WaitForNextTickAsync(token))
            {
                lock (_persistenceLock)
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
            }

            [MethodImpl(MethodImplOptions.NoInlining)]
            void ErrorPeerStorageCommit(Exception ex) => _logger.Error($"Error during peer storage commit: {ex}");
        }

        private void UpdateReputationAndMaxPeersCount()
        {
            if (!_networkConfig.IsPeersPersistenceOn) return;

            PeerAddressPermissions allowNonRoutable = _addressPolicy.AllowedAddresses;
            NetworkNode[] storedNodes = _peerStorage.GetPersistedNodes();
            Dictionary<PublicKey, NetworkNode> candidates = [with(storedNodes.Length)];
            HashSet<PublicKey> changed = [];
            int removedNodes = 0;
            foreach (NetworkNode networkNode in storedNodes)
            {
                if (!PersistedPeerPolicy.TryGetAddress(networkNode, out _, allowNonRoutable))
                {
                    if (!PersistedPeerPolicy.TryGetAddress(networkNode, out _, allowNonRoutable: PeerAddressPermissions.All))
                    {
                        _peerStorage.RemoveNode(networkNode.NodeId);
                        removedNodes++;
                    }
                    continue;
                }

                if (Peers.TryGetValue(networkNode.NodeId, out Peer? peer))
                {
                    long newRep = _stats.GetNewPersistedReputation(peer.Node);
                    if (newRep != networkNode.Reputation)
                    {
                        networkNode.Reputation = newRep;
                        changed.Add(networkNode.NodeId);
                    }
                }
                candidates[networkNode.NodeId] = networkNode;
            }

            foreach (KeyValuePair<PublicKeyAsKey, Peer> entry in Peers)
            {
                Peer peer = entry.Value;
                if (peer.OutSession is not Session { State: SessionState.Initialized, IsClosing: false, Direction: ConnectionDirection.Out, HasUsefulSyncResponse: true } session ||
                    session.RemoteNodeId != peer.Node.Id ||
                    peer.Stats?.FailedCompatibilityValidation is not null ||
                    session.RemotePort is <= 0 or > ushort.MaxValue ||
                    !IPAddress.TryParse(session.RemoteHost, out IPAddress? connectedAddress)) continue;

                IPEndPoint connectedEndpoint = new(connectedAddress.NormalizeMappedIPv4(), session.RemotePort);
                NetworkNode candidate = PersistedPeerPolicy.CreateNode(peer.Node, connectedEndpoint, _stats.GetNewPersistedReputation(peer.Node));
                if (!PersistedPeerPolicy.TryGetAddress(candidate, out _, allowNonRoutable)) continue;
                candidates[candidate.NodeId] = candidate;
                changed.Add(candidate.NodeId);
            }

            Dictionary<IPAddress, int> addressCounts = [];
            int retained = 0;
            foreach (NetworkNode candidate in candidates.Values.OrderByDescending(static n => n.Reputation))
            {
                if (retained < _networkConfig.MaxPersistedPeerCount &&
                    PersistedPeerPolicy.TryGetAddress(candidate, out IPAddress address, allowNonRoutable) &&
                    PersistedPeerPolicy.TryReserveAddress(addressCounts, address))
                {
                    retained++;
                    if (changed.Contains(candidate.NodeId)) _peerStorage.UpdateNode(candidate);
                }
                else
                {
                    _peerStorage.RemoveNode(candidate.NodeId);
                    removedNodes++;
                }
            }

            if (removedNodes > 0 && _logger.IsDebug) DebugRemovingPersistedPeers(removedNodes, storedNodes.Length);

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
            _feedTask = FeedFromNodeSource();
            StartPeerPersistenceTimer();
        }

        private async Task FeedFromNodeSource()
        {
            CancellationToken token = _cancellationTokenSource.Token;

            await foreach (Node node in _nodeSource.DiscoverNodes(token))
            {
                // Static and trusted nodes bypass throttling so they are always registered (static to stay
                // dialable, trusted so inbound connections are recognized and counted even at capacity).
                bool throttlingLogged = false;
                while (!node.IsStatic && !node.IsTrusted && (PeerCount >= _networkConfig.MaxCandidatePeerCount || ActivePeerCount >= _networkConfig.MaxActivePeers))
                {
                    if (!throttlingLogged)
                    {
                        if (_logger.IsDebug) _logger.Debug("Peer cleanup threshold reached. Throttling discovery.");
                        throttlingLogged = true;
                    }
                    await Task.Delay(TimeSpan.FromSeconds(1), _timeProvider, token);
                }

                if (!TryGetOrAdd(node, out _) && _logger.IsDebug)
                    _logger.Debug($"Refusing peer candidate because node statistics capacity is exhausted: {node:s}");
            }
        }

        public async Task StopAsync()
        {
            _nodeSource.NodeRemoved -= NodeSourceOnNodeRemoved;
            _cancellationTokenSource.Cancel();

            StopTimers();
            if (_feedTask is not null)
            {
                try { await _feedTask; }
                catch (OperationCanceledException) when (_cancellationTokenSource.IsCancellationRequested) { }
                catch (Exception exception)
                {
                    if (_logger.IsError) _logger.Error("Error during peer source stop.", exception);
                }
            }

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
            lock (_persistenceLock)
            {
                if (_peerStorage.AnyPendingChange()) _peerStorage.Commit();
            }
            foreach ((PublicKeyAsKey _, Peer peer) in Peers) peer.ReleaseStats();
            if (_logger.IsInfo) _logger.Info("Peer Pool shutdown complete.. please wait for all components to close");
        }

        string IStoppableService.Description => "peer pool";
    }
}
