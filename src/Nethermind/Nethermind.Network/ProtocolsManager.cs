/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System;
using System.Collections.Concurrent;
using System.Linq;
using Nethermind.Blockchain;
using Nethermind.Blockchain.TransactionPools;
using Nethermind.Core;
using Nethermind.Core.Logging;
using Nethermind.Network.Discovery;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.Subprotocols.Eth;
using Nethermind.Network.P2P.Subprotocols.Eth.V63;
using Nethermind.Network.Rlpx;
using Nethermind.Stats;
using Nethermind.Stats.Model;

namespace Nethermind.Network
{
    public class ProtocolsManager : IProtocolsManager
    {
        private readonly ISynchronizationManager _syncManager;
        private readonly ITransactionPool _transactionPool;
        private readonly IDiscoveryApp _discoveryApp;
        private readonly IMessageSerializationService _serializer;
        private readonly IRlpxPeer _localPeer;
        private readonly INodeStatsManager _stats;
        private readonly IProtocolValidator _protocolValidator;
        private readonly IPerfService _perfService;
        private readonly INetworkStorage _peerStorage;
        private readonly ILogManager _logManager;
        private readonly ILogger _logger;

        public ProtocolsManager(
            ISynchronizationManager synchronizationManager,
            ITransactionPool transactionPool,
            IDiscoveryApp discoveryApp,
            IMessageSerializationService serializationService,
            IRlpxPeer localPeer,
            INodeStatsManager nodeStatsManager,
            IProtocolValidator protocolValidator,
            INetworkStorage peerStorage,
            IPerfService perfService,
            ILogManager logManager)
        {
            _syncManager = synchronizationManager ?? throw new ArgumentNullException(nameof(synchronizationManager));
            _transactionPool = transactionPool ?? throw new ArgumentNullException(nameof(transactionPool));
            _discoveryApp = discoveryApp ?? throw new ArgumentNullException(nameof(discoveryApp));
            _serializer = serializationService ?? throw new ArgumentNullException(nameof(serializationService));
            _localPeer = localPeer ?? throw new ArgumentNullException(nameof(localPeer));
            _stats = nodeStatsManager ?? throw new ArgumentNullException(nameof(nodeStatsManager));
            _protocolValidator = protocolValidator ?? throw new ArgumentNullException(nameof(protocolValidator));
            _perfService = perfService ?? throw new ArgumentNullException(nameof(perfService));
            _peerStorage = peerStorage ?? throw new ArgumentNullException(nameof(peerStorage));
            _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
            _logger = _logManager.GetClassLogger();

            _syncManager.SyncEvent += OnSyncEvent;
            localPeer.SessionCreated += SessionCreated;
        }

        private NodeStatsEventType GetSyncEventType(SyncStatus syncStatus)
        {
            switch (syncStatus)
            {
                case SyncStatus.InitCompleted:
                    return NodeStatsEventType.SyncInitCompleted;
                case SyncStatus.InitCancelled:
                    return NodeStatsEventType.SyncInitCancelled;
                case SyncStatus.InitFailed:
                    return NodeStatsEventType.SyncInitFailed;
                case SyncStatus.Started:
                    return NodeStatsEventType.SyncStarted;
                case SyncStatus.Completed:
                    return NodeStatsEventType.SyncCompleted;
                case SyncStatus.Failed:
                    return NodeStatsEventType.SyncFailed;
                case SyncStatus.Cancelled:
                    return NodeStatsEventType.SyncCancelled;
            }

            throw new Exception($"SyncStatus not supported: {syncStatus.ToString()}");
        }

        private ConcurrentDictionary<Guid, ISession> _sessions = new ConcurrentDictionary<Guid, ISession>();

        [Todo(Improve.Refactor, "this can be all in SyncManager now")]
        private void OnSyncEvent(object sender, SyncEventArgs e)
        {
            if (_logger.IsTrace) _logger.Trace($"|NetworkTrace| sync event {e.SyncStatus.ToString()} on {e.Peer.Node:s}");

            if (!_sessions.TryGetValue(e.Peer.SessionId, out ISession session))
            {
                if (_logger.IsTrace) _logger.Trace($"Sync failed for an unknown session {e.Peer.Node:s} {e.Peer.SessionId}");
                return;
            }

            var nodeStatsEvent = GetSyncEventType(e.SyncStatus);
            _stats.ReportSyncEvent(session.Node, nodeStatsEvent, new SyncNodeDetails
            {
                NodeBestBlockNumber = e.NodeBestBlockNumber,
                OurBestBlockNumber = e.OurBestBlockNumber
            });

            if (new[] {SyncStatus.InitFailed, SyncStatus.InitCancelled, SyncStatus.Failed, SyncStatus.Cancelled}.Contains(e.SyncStatus))
            {
                if (_logger.IsDebug) _logger.Debug($"Initializing disconnect {session} on sync {e.SyncStatus.ToString()} with {e.Peer.Node:s}");
                session.InitiateDisconnect(DisconnectReason.Other);
            }
        }

        private void SessionCreated(object sender, SessionEventArgs e)
        {
            _sessions.TryAdd(e.Session.SessionId, e.Session);
            e.Session.Initialized += SessionInitialized;
            e.Session.Disconnected += SessionDisconnected;
        }

        private void SessionDisconnected(object sender, DisconnectEventArgs e)
        {
            ISession session = (ISession) sender;
            session.Initialized -= SessionInitialized;
            session.Disconnected -= SessionDisconnected;
            
            if (_syncPeers.ContainsKey(session.SessionId))
            {
                ISynchronizationPeer syncPeer = _syncPeers[session.SessionId];
                _syncManager.RemovePeer(syncPeer);
                _transactionPool.RemovePeer(syncPeer.Node.Id);
                if(_logger.IsWarn) _logger.Warn($"Sync peer {session} disconnected {e.DisconnectType} {e.DisconnectReason}");
            }
            
            _sessions.TryRemove(session.SessionId, out session);
        }

        private ConcurrentDictionary<Guid, Eth62ProtocolHandler> _syncPeers = new ConcurrentDictionary<Guid, Eth62ProtocolHandler>();

        private void SessionInitialized(object sender, EventArgs e)
        {
            ISession session = (ISession) sender;
            InitProtocol(session, Protocol.P2P, session.P2PVersion);
        }

        private void InitProtocol(ISession session, string protocolCode, int version)
        {
            if (session.State < SessionState.Initialized)
            {
                throw new InvalidOperationException($"{nameof(InitProtocol)} called on {session}");
            }

            if (session.State != SessionState.Initialized)
            {
                return;
            }

            protocolCode = protocolCode.ToLowerInvariant();
            IProtocolHandler protocolHandler;
            switch (protocolCode)
            {
                case Protocol.P2P:
                    P2PProtocolHandler handler = new P2PProtocolHandler(session, _localPeer.LocalNodeId, _stats, _serializer, _perfService, _logManager);
                    session.PingSender = handler;
                    InitP2PProtocol(session, handler);
                    protocolHandler = handler;
                    break;
                case Protocol.Eth:
                    if (version < 62 || version > 63)
                    {
                        throw new NotSupportedException($"Eth protocol version {version} is not supported.");
                    }

                    Eth62ProtocolHandler ethHandler = version == 62
                        ? new Eth62ProtocolHandler(session, _serializer, _stats, _syncManager, _logManager, _perfService, _transactionPool)
                        : new Eth63ProtocolHandler(session, _serializer, _stats, _syncManager, _logManager, _perfService, _transactionPool);
                    InitEthProtocol(session, ethHandler);
                    protocolHandler = ethHandler;
                    break;
                default:
                    throw new NotSupportedException($"Protocol {protocolCode} {version} is not supported");
            }

            protocolHandler.SubprotocolRequested += (sender, args) => InitProtocol(session, args.ProtocolCode, args.Version);
            session.AddProtocolHandler(protocolHandler);
            protocolHandler.Init();
        }
        
        private void InitP2PProtocol(ISession session, P2PProtocolHandler handler)
        {
            handler.ProtocolInitialized += (sender, args) =>
            {
                P2PProtocolInitializedEventArgs typedArgs = (P2PProtocolInitializedEventArgs) args;
                if (!RunBasicChecks(session, Protocol.P2P, handler.ProtocolVersion)) return;

                if (handler.ProtocolVersion >= 5)
                {
                    if (_logger.IsTrace) _logger.Trace($"{handler.ProtocolCode}.{handler.ProtocolVersion} established on {session} - enabling snappy");
                    session.EnableSnappy();
                }
                else
                {
                    if (_logger.IsTrace) _logger.Trace($"{handler.ProtocolCode}.{handler.ProtocolVersion} established on {session} - disabling snappy");
                }

                _stats.ReportP2PInitializationEvent(session.Node, new P2PNodeDetails
                {
                    ClientId = typedArgs.ClientId,
                    Capabilities = typedArgs.Capabilities.ToArray(),
                    P2PVersion = typedArgs.P2PVersion,
                    ListenPort = typedArgs.ListenPort
                });

                AddNodeToDiscovery(session, typedArgs);

                _protocolValidator.DisconnectOnInvalid(Protocol.P2P, session, args);

                if (_logger.IsTrace) _logger.Trace($"Finalized P2P protocol initialization on {session}");
            };
        }

        private void InitEthProtocol(ISession session, Eth62ProtocolHandler handler)
        {
            handler.ProtocolInitialized += (sender, args) =>
            {
                if (!RunBasicChecks(session, handler.ProtocolCode, handler.ProtocolVersion)) return;
                var typedArgs = (EthProtocolInitializedEventArgs)args;
                _stats.ReportEthInitializeEvent(session.Node, new EthNodeDetails
                {
                    ChainId = typedArgs.ChainId,
                    BestHash = typedArgs.BestHash,
                    GenesisHash = typedArgs.GenesisHash,
                    ProtocolVersion = typedArgs.ProtocolVersion,
                    TotalDifficulty = typedArgs.TotalDifficulty
                });

                bool isValid = _protocolValidator.DisconnectOnInvalid(Protocol.Eth, session, args);
                if (isValid)
                {
                    handler.ClientId = session.Node.ClientId;
                    
                    if (_syncPeers.TryAdd(session.SessionId, handler))
                    {
                        _syncManager.AddPeer(handler);
                        _transactionPool.AddPeer(handler);
                        if(_logger.IsWarn) _logger.Warn($"Sync peer {session} created.");
                    }
                    else
                    {
                        if (_logger.IsTrace) _logger.Trace($"Not able to add a sync peer on {session} for {session.Node:s}");
                        session.InitiateDisconnect(DisconnectReason.AlreadyConnected);
                    }

                    if (_logger.IsTrace) _logger.Trace($"Finalized ETH protocol initialization on {session} - adding sync peer {session.Node:s}");

                    //Add/Update peer to the storage and to sync manager
                    _peerStorage.UpdateNodes(new[] {new NetworkNode(session.Node.Id, session.Node.Host, session.Node.Port, _stats.GetOrAdd(session.Node).NewPersistedNodeReputation)});
                }
                else
                {
                    if (_logger.IsTrace) _logger.Trace($"|NetworkTrace| {handler.ProtocolCode}{handler.ProtocolVersion} is invalid on {session}");
                }
            };
        }

        private bool RunBasicChecks(ISession session, string protocolCode, int protocolVersion)
        {
            if (session.IsClosing)
            {
                if (_logger.IsDebug) _logger.Debug($"|NetworkTrace| {protocolCode}.{protocolVersion} initialized in {session}");
                return false;
            }

            if (_logger.IsTrace) _logger.Trace($"|NetworkTrace| {protocolCode}.{protocolVersion} initialized in {session}");
            return true;
        }

        /// <summary>
        /// In case of IN connection we don't know what is the port node is listening on until we receive the Hello message
        /// </summary>
        private void AddNodeToDiscovery(ISession session, P2PProtocolInitializedEventArgs eventArgs)
        {
            if (eventArgs.ListenPort == 0)
            {
                if (_logger.IsTrace) _logger.Trace($"Listen port is 0, node is not listening: {session}");
                return;
            }

            if (session.Node.Port != eventArgs.ListenPort)
            {
                if (_logger.IsDebug) _logger.Debug($"Updating listen port for {session:s} to: {eventArgs.ListenPort}");

                if (session.Node.AddedToDiscovery)
                {
                    if (_logger.IsDebug) _logger.Debug($"Discovery node already initialized with wrong port {session} - listen port: {eventArgs.ListenPort}");
                }

                session.Node.Port = eventArgs.ListenPort;
            }

            if (session.Node.AddedToDiscovery)
            {
                return;
            }

            //In case peer was initiated outside of discovery and discovery is enabled, we are adding it to discovery for future use (e.g. trusted peer)
            _discoveryApp.AddNodeToDiscovery(session.Node);
            session.Node.AddedToDiscovery = true;
        }
    }
}