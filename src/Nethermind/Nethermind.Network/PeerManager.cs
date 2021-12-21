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
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;
using Nethermind.Core;
using Nethermind.Core.Attributes;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.Network.Config;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.EventArg;
using Nethermind.Network.Rlpx;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using Timer = System.Timers.Timer;

namespace Nethermind.Network
{
    /// <summary>
    /// </summary>
    [SuppressMessage("ReSharper", "InconsistentNaming")]
    public partial class PeerManager : IPeerManager
    {
        private readonly ILogger _logger;
        private readonly INetworkConfig _networkConfig;
        private readonly IRlpxHost _rlpxHost;
        private readonly INodeStatsManager _stats;
        private readonly IPeerPool _peerPool;

        private int _tryCount;
        private int _newActiveNodes;
        private int _failedInitialConnect;
        private Task? _mainLoopTask;

        private readonly CancellationTokenSource _cancellationTokenSource = new();
        private static readonly int _parallelism = Environment.ProcessorCount;

        public PeerManager(
            IRlpxHost rlpxHost,
            IPeerPool peerPool,
            INodeStatsManager stats,
            INetworkConfig networkConfig,
            ILogManager logManager)
        {
            _logger = logManager.GetClassLogger();
            _rlpxHost = rlpxHost ?? throw new ArgumentNullException(nameof(rlpxHost));
            _stats = stats ?? throw new ArgumentNullException(nameof(stats));
            _networkConfig = networkConfig ?? throw new ArgumentNullException(nameof(networkConfig));
            _peerPool = peerPool;
        }

        public IReadOnlyCollection<Peer> ActivePeers => _peerPool.ActivePeers.Values.ToList();
        public IReadOnlyCollection<Peer> CandidatePeers => _peerPool.Peers.Values.ToList();
        public IReadOnlyCollection<Peer> ConnectedPeers =>
            _peerPool.ActivePeers.Values.Where(p => p.IsConnected).ToList();

        public int MaxActivePeers => _networkConfig.ActivePeersMaxCount + _peerPool.StaticPeerCount;
        private int AvailableActivePeersCount => MaxActivePeers - _peerPool.ActivePeerCount;

        public void Start()
        {
            _peerPool.PeerAdded += PeerPoolOnPeerAdded;
            _peerPool.PeerRemoved += PeerPoolOnPeerRemoved;

            _rlpxHost.SessionCreated += (_, args) =>
            {
                ToggleSessionEventListeners(args.Session, true);
                SessionCreated sessionCreated = new(this, args.Session);
                _peeringEvents.Add(sessionCreated);
            };

            MorePeersNeeded morePeersNeeded = new MorePeersNeeded(this);
            _peeringEvents.Add(morePeersNeeded);
            
            updatePeersTimer = new Timer();
            updatePeersTimer.Interval = 1000;
            updatePeersTimer.Elapsed += UpdatePeersTimerOnElapsed;
            updatePeersTimer.Start();

            _mainLoopTask = Task.Factory.StartNew(
                RunMainLoop,
                _cancellationTokenSource.Token,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default).ContinueWith(t =>
            {
                if (t.IsFaulted)
                {
                    if (_logger.IsError) _logger.Error("Peer update loop encountered an exception.", t.Exception);
                }
                else if (t.IsCanceled)
                {
                    if (_logger.IsDebug) _logger.Debug("Peer update loop stopped.");
                }
                else if (t.IsCompleted)
                {
                    if (_logger.IsDebug) _logger.Debug("Peer update loop complete.");
                }
            });
        }

        public async Task StopAsync()
        {
            updatePeersTimer?.Stop();
            updatePeersTimer?.Dispose();

            _cancellationTokenSource.Cancel();
            await (_mainLoopTask ?? Task.CompletedTask);

            if (_logger.IsInfo)
                _logger.Info("Peer Manager shutdown complete.. please wait for all components to close");
        }

        private void PeerPoolOnPeerAdded(object sender, PeerEventArgs nodeEventArgs)
        {
            PeerAdded peerAdded = new(this, nodeEventArgs.Peer);
            _peeringEvents.Add(peerAdded);
        }

        private void PeerPoolOnPeerRemoved(object? sender, PeerEventArgs e)
        {
            PeerRemoved peerRemoved = new(this, e.Peer);
            _peeringEvents.Add(peerRemoved);

            MorePeersNeeded morePeersNeeded = new(this);
            _peeringEvents.Add(morePeersNeeded);
        }

        private void UpdatePeersTimerOnElapsed(object? sender, ElapsedEventArgs e)
        {
            MorePeersNeeded morePeersNeeded = new(this);
            _peeringEvents.Add(morePeersNeeded);
        }

        [Todo(Improve.MissingFunctionality,
            "Add cancellation support for the peer connection (so it does not wait for the 10sec timeout")]
        private async Task SetupPeerConnection(Peer peer)
        {
            // TODO: this one still runs in parallel - needs to fix this
            // pending connections here

            if (!_peerPool.Peers.ContainsKey(peer.Node.Id))
            {
                return;
            }

            if (_peerPool.ActivePeers.ContainsKey(peer.Node.Id))
            {
                return;
            }

            if (AvailableActivePeersCount <= 0)
            {
                return;
            }

            // Can happen when In connection is received from the same peer and is initialized before we get here
            // In this case we do not initialize OUT connection
            if (!AddActivePeer(peer.Node.Id, peer, "upgrading candidate"))
            {
                if (_logger.IsTrace) _logger.Trace($"Active peer was already added to collection: {peer.Node.Id}");
                return;
            }

            Interlocked.Increment(ref _tryCount);
            bool result = await InitializePeerConnection(peer);
            // for some time we will have a peer in active that has no session assigned - analyze this?
            if (_logger.IsTrace)
                _logger.Trace(
                    $"Connecting to {_stats.GetCurrentReputation(peer.Node)} rep node - {result}, ACTIVE: {_peerPool.ActivePeerCount}, CAND: {_peerPool.PeerCount}");

            if (!result)
            {
                _stats.ReportEvent(peer.Node, NodeStatsEventType.ConnectionFailed);
                Interlocked.Increment(ref _failedInitialConnect);
                if (peer.OutSession != null)
                {
                    if (_logger.IsTrace) _logger.Trace($"Timeout, doing additional disconnect: {peer.Node.Id}");
                    peer.OutSession?.MarkDisconnected(DisconnectReason.ReceiveMessageTimeout, DisconnectType.Local,
                        "timeout");
                }

                peer.IsAwaitingConnection = false;
                DeactivatePeerIfDisconnected(peer, "Failed to initialize connections");

                return;
            }

            Interlocked.Increment(ref _newActiveNodes);
        }

        private bool AddActivePeer(PublicKey nodeId, Peer peer, string reason)
        {
            Console.WriteLine($"adding active {peer} {reason}");
            peer.IsAwaitingConnection = false;
            bool added = _peerPool.ActivePeers.TryAdd(nodeId, peer);
            if (added)
            {
                if (_logger.IsTrace) _logger.Trace($"|NetworkTrace| {peer.Node:s} added to active peers - {reason}");
            }
            else
            {
                if (_logger.IsTrace) _logger.Trace($"|NetworkTrace| {peer.Node:s} already in active peers");
            }

            return added;
        }

        private bool RemoveActivePeer(PublicKey nodeId, string reason)
        {
            bool removed = _peerPool.ActivePeers.TryRemove(nodeId, out Peer peer);
            if (removed)
            {
                if (_logger.IsTrace) _logger.Trace($"|NetworkTrace| {peer.Node:s} removed from active peers - {reason}");
            }
            else
            {
                if (_logger.IsTrace) _logger.Trace($"|NetworkTrace| {nodeId} was not an active peer - skipping removal");
            }

            return removed;
        }

        private void DeactivatePeerIfDisconnected(Peer peer, string reason)
        {
            if (_logger.IsTrace) _logger.Trace($"DEACTIVATING IF DISCONNECTED {peer} {reason}");
            if (!peer.IsConnected && !peer.IsAwaitingConnection)
            {
                // dropping references to sessions so they can be garbage collected
                peer.InSession = null;
                peer.OutSession = null;
                RemoveActivePeer(peer.Node.Id, reason);
            }
        }

        private async Task<bool> InitializePeerConnection(Peer candidate)
        {
            try
            {
                if (_logger.IsTrace) _logger.Trace($"CONNECTING TO {candidate}");
                candidate.IsAwaitingConnection = true;
                await _rlpxHost.ConnectAsync(candidate.Node);
                return true;
            }
            catch (NetworkingException ex)
            {
                if (_logger.IsTrace)
                    _logger.Trace($"Cannot connect to peer [{ex.NetworkExceptionType.ToString()}]: {candidate.Node:s}");
                return false;
            }
            catch (Exception ex)
            {
                if (_logger.IsDebug)
                    _logger.Error($"Error trying to initiate connection with peer: {candidate.Node:s}", ex);
                return false;
            }
        }

        /// <summary>
        /// When both outgoing and incoming connections to the same peer are created then we choose which one to keep
        /// based on their node ID comparison and then drop on of the sessions.
        /// </summary>
        /// <param name="remoteNode">Node that we establish connection with</param>
        /// <returns>Direction (IN or OUT) to keep. The other direction should get disconnected.</returns>
        private ConnectionDirection ChooseDirectionToKeep(PublicKey remoteNode)
        {
            if (_logger.IsTrace) _logger.Trace($"CHOOSING DIRECTION {remoteNode}");
            byte[] localKey = _rlpxHost.LocalNodeId.Bytes;
            byte[] remoteKey = remoteNode.Bytes;
            for (int i = 0; i < remoteNode.Bytes.Length; i++)
            {
                if (localKey[i] > remoteKey[i])
                {
                    return ConnectionDirection.Out;
                }

                if (localKey[i] < remoteKey[i])
                {
                    return ConnectionDirection.In;
                }
            }

            return ConnectionDirection.In;
        }

        private void AddSession(ISession session, Peer peer)
        {
            void SetSessionOnPeer(ConnectionDirection connectionDirection)
            {
                if (connectionDirection == ConnectionDirection.In)
                {
                    _stats.ReportHandshakeEvent(peer.Node, ConnectionDirection.In);
                    peer.InSession = session;
                }
                else
                {
                    peer.OutSession = session;
                }
            }

            if (_logger.IsTrace) _logger.Trace($"ADDING {session} {peer}");
            bool newSessionIsIn = session.Direction == ConnectionDirection.In;
            bool newSessionIsOut = !newSessionIsIn;
            bool peerIsDisconnected = !peer.IsConnected;

            if (peerIsDisconnected || (peer.IsAwaitingConnection && session.Direction == ConnectionDirection.Out))
            {
                SetSessionOnPeer(session.Direction);
            }
            else
            {
                bool peerHasAnOpenInSession = !peer.InSession?.IsClosing ?? false;
                bool peerHasAnOpenOutSession = !peer.OutSession?.IsClosing ?? false;

                bool peerHasAnOpenSessionInTheSameDirection =
                    newSessionIsIn && peerHasAnOpenInSession || newSessionIsOut && peerHasAnOpenOutSession;

                bool peerHasAnOpenSessionInTheOppositeDirection =
                    newSessionIsIn && peerHasAnOpenOutSession || newSessionIsOut && peerHasAnOpenInSession;

                ISession? sessionToDisconnect = null;
                if (peerHasAnOpenSessionInTheSameDirection)
                {
                    // we disconnect the later session in the same direction (we keep the old connection)
                    sessionToDisconnect = session;
                }
                else if (peerHasAnOpenSessionInTheOppositeDirection)
                {
                    // we will store information both on IN and OUT sessions for the peer
                    SetSessionOnPeer(session.Direction);
                    ConnectionDirection directionToKeep = ChooseDirectionToKeep(session.RemoteNodeId);
                    bool newSessionLost = session.Direction != directionToKeep;

                    if (newSessionLost)
                    {
                        sessionToDisconnect = session;
                    }
                    // replacing existing session with the new one as the new one won
                    else
                    {
                        sessionToDisconnect = newSessionIsIn ? peer.OutSession : peer.InSession;
                    }
                }
                
                sessionToDisconnect?.InitiateDisconnect(DisconnectReason.AlreadyConnected, "same");
            }

            AddActivePeer(peer.Node.Id, peer, newSessionIsIn ? "new IN session" : "new OUT session");
        }

        #region Session related events

        private void ToggleSessionEventListeners(ISession session, bool shouldListen)
        {
            if (shouldListen)
            {
                session.HandshakeComplete += OnHandshakeComplete;
                session.Disconnected += OnDisconnected;
            }
            else
            {
                session.HandshakeComplete -= OnHandshakeComplete;
                session.Disconnected -= OnDisconnected;
            }
        }

        private void OnDisconnected(object sender, DisconnectEventArgs e)
        {
            ToggleSessionEventListeners((ISession)sender, false);
            
            SessionDisconnected sessionDisconnected = new(this, (ISession)sender, e.DisconnectType, e.DisconnectReason);
            _peeringEvents.Add(sessionDisconnected);

            MorePeersNeeded morePeersNeeded = new(this);
            _peeringEvents.Add(morePeersNeeded);
        }

        private void OnHandshakeComplete(object sender, EventArgs args)
        {
            HandshakeCompleted handshakeCompleted = new(this, (ISession)sender);
            _peeringEvents.Add(handshakeCompleted);
        }

        #endregion

        private readonly BlockingCollection<PeeringEvent> _peeringEvents = new(new ConcurrentQueue<PeeringEvent>());
        private Timer? updatePeersTimer;

        private void RunMainLoop()
        {
            Type lastEventType = null;
            long lastEventTime = Timestamper.Default.UnixTime.MillisecondsLong;

            foreach (PeeringEvent peeringEvent in _peeringEvents.GetConsumingEnumerable(_cancellationTokenSource.Token))
            {
                Console.WriteLine(peeringEvent);
                Type thisEventType = peeringEvent.GetType();
                long thisEventTime = Timestamper.Default.UnixTime.MillisecondsLong;
                long timeElapsed = thisEventTime - lastEventTime;

                // if the last event within the last 1 second was also MorePeersNeeded then skip
                if (timeElapsed < 1000 && thisEventType == typeof(MorePeersNeeded) && lastEventType == thisEventType)
                {
                    continue;
                }

                if(_logger.IsTrace) _logger.Trace($"Executing {peeringEvent}");
                Console.WriteLine($"{peeringEvent} EXECUTES");
                peeringEvent.Execute();
                lastEventType = thisEventType;
                lastEventTime = thisEventTime;
            }
        }
    }
}
