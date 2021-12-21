//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Config;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.Network.Config;
using Nethermind.Network.P2P;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using Timer = System.Timers.Timer;

namespace Nethermind.Network
{
    /// <summary>
    /// This class is responsible for gathering all the nodes discovered in various node sources.
    /// This class is also responsible for persisting peers information.
    ///
    /// Responsibility that should disappear from here is knowing which peers are active.
    /// </summary>
    public class PeerPool : IPeerPool
    {
        private readonly CancellationTokenSource _cancellationTokenSource = new();
        private Timer _peerPersistenceTimer;
        private Task? _storageCommitTask;
        
        private readonly INodeSource _nodeSource;
        private readonly INodeStatsManager _stats;
        private readonly INetworkStorage _peerStorage;
        private readonly INetworkConfig _networkConfig;
        private readonly ILogger _logger;
        
        public ConcurrentDictionary<PublicKey, Peer> ActivePeers { get; } = new();
        public ConcurrentDictionary<PublicKey, Peer> Peers { get; } = new();
        private readonly ConcurrentDictionary<PublicKey, Peer> _staticPeers = new();
        
        public List<Peer> NonStaticPeers => Peers.Values.Where(p => !p.Node.IsStatic).ToList();
        public List<Peer> StaticPeers => _staticPeers.Values.ToList();
        
        public int PeerCount => Peers.Count;
        public int ActivePeerCount => ActivePeers.Count;
        public int StaticPeerCount => _staticPeers.Count;
        
        public event EventHandler<PeerEventArgs>? PeerAdded;
        public event EventHandler<PeerEventArgs>? PeerRemoved;

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

        public Peer GetOrAdd(Node node, [CallerMemberName] string member = null)
        {
            Console.WriteLine($"GET OR ADD {member}");
            return Peers.GetOrAdd(node.Id, CreateNew, (node, _staticPeers));
        }
        
        private Peer CreateNew(PublicKey key, (Node Node, ConcurrentDictionary<PublicKey, Peer> Statics) arg)
        {
            if (arg.Node.IsBootnode || arg.Node.IsStatic)
            {
                if (_logger.IsDebug) _logger.Debug(
                    $"Adding a {(arg.Node.IsBootnode ? "bootnode" : "stored")} candidate peer {arg.Node:s}");
            }
            
            Peer peer = new(arg.Node);
            if (arg.Node.IsStatic)
            {
                arg.Statics.TryAdd(arg.Node.Id, peer);
            }

            PeerAdded?.Invoke(this, new PeerEventArgs(peer));
            return peer;
        }

        public bool TryGet(PublicKey id, out Peer peer)
        {
            return Peers.TryGetValue(id, out peer);
        }

        public bool TryRemove(PublicKey id, out Peer peer)
        {
            _staticPeers.TryRemove(id, out _);
            if (Peers.TryRemove(id, out peer))
            {
                peer.InSession?.MarkDisconnected(DisconnectReason.DisconnectRequested, DisconnectType.Local, "admin_removePeer");
                peer.OutSession?.MarkDisconnected(DisconnectReason.DisconnectRequested, DisconnectType.Local, "admin_removePeer");
                peer.InSession = null;
                peer.OutSession = null;
                PeerRemoved?.Invoke(this, new PeerEventArgs(peer));
                return true;
            }

            return false;
        }

        public Peer Replace(ISession session)
        {
            if (session.ObsoleteRemoteNodeId is null)
            {
                throw new Exception(
                    $"{nameof(Replace)} should never be called on session with a NULL {nameof(session.ObsoleteRemoteNodeId)}");
            }
            
            if (Peers.TryGetValue(session.ObsoleteRemoteNodeId, out Peer previousPeer))
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
                    if (Peers.TryRemove(session.ObsoleteRemoteNodeId, out Peer? oldPeer))
                    {
                        PeerRemoved?.Invoke(this, new PeerEventArgs(oldPeer!));
                        // oldPeer!.InSession?.InitiateDisconnect(DisconnectReason.UnexpectedIdentity, "");
                        // oldPeer.OutSession?.InitiateDisconnect(DisconnectReason.UnexpectedIdentity, "");
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

        private void StartPeerPersistenceTimer()
        {
            if (_logger.IsDebug) _logger.Debug("Starting peer persistence timer");

            _peerPersistenceTimer = new Timer(_networkConfig.PeersPersistenceInterval)
            {
                AutoReset = false
            };
            
            _peerPersistenceTimer.Elapsed += (sender, e) =>
            {
                try
                {
                    _peerPersistenceTimer.Enabled = false;
                    RunPeerCommit();
                }
                catch (Exception exception)
                {
                    if (_logger.IsDebug) _logger.Error("Peer persistence timer failed", exception);
                }
                finally
                {
                    _peerPersistenceTimer.Enabled = true;
                }
            };

            _peerPersistenceTimer.Start();
        }
        
        private void RunPeerCommit()
        {
            try
            {
                UpdateReputationAndMaxPeersCount();

                if (!_peerStorage.AnyPendingChange())
                {
                    if (_logger.IsTrace) _logger.Trace("No changes in peer storage, skipping commit.");
                    return;
                }

                _storageCommitTask = Task.Run(() =>
                {
                    _peerStorage.Commit();
                    _peerStorage.StartBatch();
                });


                Task task = _storageCommitTask.ContinueWith(x =>
                {
                    if (x.IsFaulted && _logger.IsError)
                    {
                        _logger.Error($"Error during peer storage commit: {x.Exception}");
                    }
                });
                task.Wait();
                _storageCommitTask = null;
            }
            catch (Exception ex)
            {
                _logger.Error($"Error during peer storage commit: {ex}");
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

                Node node = new (networkNode);
                Peer peer = GetOrAdd(node);
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
                _peerPersistenceTimer?.Stop();
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
                    if (x.IsFaulted)
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
