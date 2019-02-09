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
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Threading.Tasks;
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
        private readonly IBlockTree _blockTree;
        private readonly ITransactionPool _transactionPool;
        private readonly IDiscoveryApp _discoveryApp;
        private readonly IMessageSerializationService _serializer;
        private readonly INodeStatsManager _stats;
        private readonly IProtocolValidator _protocolValidator;
        private readonly IPerfService _perfService;
        private readonly INetworkStorage _peerStorage;
        private readonly ILogManager _logManager;
        private readonly ITimestamp _timestamp = new Timestamp();
        private readonly ILogger _logger;

        public ProtocolsManager(
            IRlpxPeer localPeer,
            ISynchronizationManager synchronizationManager,
            IBlockTree blockTree,
            ITransactionPool transactionPool,
            IDiscoveryApp discoveryApp,
            IMessageSerializationService serializationService,
            INodeStatsManager nodeStatsManager,
            IProtocolValidator protocolValidator,
            IPerfService perfService,
            INetworkStorage peerStorage,
            ILogManager logManager)
        {
            _syncManager = synchronizationManager ?? throw new ArgumentNullException(nameof(synchronizationManager));
            _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
            _transactionPool = transactionPool ?? throw new ArgumentNullException(nameof(transactionPool));
            _discoveryApp = discoveryApp ?? throw new ArgumentNullException(nameof(discoveryApp));
            _serializer = serializationService ?? throw new ArgumentNullException(nameof(serializationService));
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

        private ConcurrentDictionary<Guid, IP2PSession> _sessions = new ConcurrentDictionary<Guid, IP2PSession>();

        private void OnSyncEvent(object sender, SyncEventArgs e)
        {
            if (_logger.IsTrace) _logger.Trace($"|NetworkTrace| Sync Event: {e.SyncStatus.ToString()}, NodeId: {e.Peer.Node.Id}");

            if (!_sessions.TryGetValue(e.Peer.Id, out IP2PSession session))
            {
                if (_logger.IsTrace) _logger.Trace($"Sync failed, peer not in active collection: {e.Peer.Node.Id}");
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
                if (_logger.IsDebug) _logger.Debug($"Initializing disconnect on sync {e.SyncStatus.ToString()} with node: {e.Peer.Node.Id}");
                session.InitiateDisconnectAsync(DisconnectReason.Other).ContinueWith(
                    t =>
                    {
                        if (_logger.IsDebug) _logger.Debug($"Failed to disconnect a session after a synchronizaton failure {session.RemoteNodeId}");
                    });
            }
        }

        private void SessionCreated(object sender, SessionEventArgs e)
        {
            e.Session.Initialized += SessionInitialized;
            e.Session.Disconnected += SessionDisconnected;
            e.Session.Disconnecting += SessionDisconnecting;
        }

        private void SessionDisconnecting(object sender, DisconnectEventArgs e)
        {
            IP2PSession session = (IP2PSession) sender;
            if (_syncPeers.ContainsKey(session.SessionId))
            {
                ISynchronizationPeer syncPeer = _syncPeers[session.SessionId];
                _syncManager.RemovePeer(syncPeer);
                _transactionPool.AddPeer(syncPeer);
            }
        }

        private void SessionDisconnected(object sender, DisconnectEventArgs e)
        {
            IP2PSession session = (IP2PSession) sender;
            session.Initialized -= SessionInitialized;
            session.Disconnected -= SessionDisconnected;
            session.Disconnecting -= SessionDisconnecting;
        }

        private ConcurrentDictionary<Guid, Eth62ProtocolHandler> _syncPeers = new ConcurrentDictionary<Guid, Eth62ProtocolHandler>();

        private void SessionInitialized(object sender, EventArgs e)
        {
            IP2PSession session = (IP2PSession) sender;
            InitProtocol(session, Protocol.P2P, session.P2PVersion);
        }

        private void InitProtocol(IP2PSession session, string protocolCode, int version)
        {
            if (session.SessionState < SessionState.Initialized)
            {
                throw new InvalidOperationException($"{nameof(InitProtocol)} called on session that is in the {session.SessionState} state");
            }

            if (session.SessionState != SessionState.Initialized)
            {
                return;
            }

            protocolCode = protocolCode.ToLowerInvariant();
            IProtocolHandler protocolHandler;
            switch (protocolCode)
            {
                case Protocol.P2P:
                    P2PProtocolHandler handler = new P2PProtocolHandler(session, _stats, _serializer, _perfService, _logManager);
                    session.P2PMessageSender = handler;
                    InitP2PProtocol(session, handler);
                    protocolHandler = handler;
                    break;
                case Protocol.Eth:
                    if (version < 62 || version > 63)
                    {
                        throw new NotSupportedException($"Eth protocol version {version} is not supported.");
                    }

                    Eth62ProtocolHandler ethHandler = version == 62
                        ? new Eth62ProtocolHandler(session, _serializer, _stats, _syncManager, _logManager, _perfService, _blockTree, _transactionPool, _timestamp)
                        : new Eth63ProtocolHandler(session, _serializer, _stats, _syncManager, _logManager, _perfService, _blockTree, _transactionPool, _timestamp);
                    InitEthProtocol(session, ethHandler);
                    protocolHandler = ethHandler;
                    break;
                default:
                    throw new NotSupportedException();
            }

            protocolHandler.SubprotocolRequested += (sender, args) => InitProtocol(session, args.ProtocolCode, args.Version);
            session.AddProtocolHandler(protocolHandler);
            protocolHandler.Init();
        }

        private void InitP2PProtocol(IP2PSession session, P2PProtocolHandler handler)
        {
            handler.ProtocolInitialized += (sender, args) =>
            {
                if (RunBasicChecks(session, handler.ProtocolCode, handler.ProtocolVersion)) return;
                P2PProtocolInitializedEventArgs typedArgs = (P2PProtocolInitializedEventArgs) args;
                if (RunBasicChecks(session, Protocol.P2P, handler.ProtocolVersion)) return;

                if (handler.ProtocolVersion >= 5)
                {
                    if (_logger.IsTrace) _logger.Trace($"{session.RemoteNodeId} {handler.ProtocolCode} v{handler.ProtocolVersion} established - Enabling Snappy");
                    session.EnableSnappy();
                }
                else
                {
                    if (_logger.IsTrace) _logger.Trace($"{session.RemoteNodeId} {handler.ProtocolCode} v{handler.ProtocolVersion} established - Disabling Snappy");
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

                if (_logger.IsTrace) _logger.Trace($"P2P Protocol Initialized: {session.RemoteNodeId}");
            };
        }

        private void InitEthProtocol(IP2PSession session, Eth62ProtocolHandler handler)
        {
            handler.ProtocolInitialized += (sender, args) =>
            {
                if (RunBasicChecks(session, handler.ProtocolCode, handler.ProtocolVersion)) return;
                var ethEventArgs = (EthProtocolInitializedEventArgs) e;
                _stats.ReportEthInitializeEvent(session.Node, new EthNodeDetails
                {
                    ChainId = ethEventArgs.ChainId,
                    BestHash = ethEventArgs.BestHash,
                    GenesisHash = ethEventArgs.GenesisHash,
                    Protocol = ethEventArgs.Protocol,
                    ProtocolVersion = ethEventArgs.ProtocolVersion,
                    TotalDifficulty = ethEventArgs.TotalDifficulty
                });

                bool isValid = _protocolValidator.DisconnectOnInvalid(Protocol.Eth, session, e);
                if (isValid)
                {
                    if (_syncPeers.TryAdd(session.SessionId, handler))
                    {
                        _syncManager.AddPeer(handler);
                        _transactionPool.AddPeer(handler);
                    }
                    else
                    {
                        session.DisconnectAsync(DisconnectReason.Other, DisconnectType.Local).ContinueWith(t =>
                        {
                            if (t.IsFaulted)
                            {
                                if (_logger.IsDebug) _logger.Debug($"Failed to disconnect {session.RemoteNodeId}");
                            }
                        });
                    }

                    handler.ClientId = session.Node.ClientId;

                    if (_logger.IsTrace) _logger.Trace($"Eth version {handler.ProtocolVersion} initialized, adding sync peer: {session.Node.Id}");

                    //Add/Update peer to the storage and to sync manager
                    _peerStorage.UpdateNodes(new[] {new NetworkNode(session.Node.Id, session.Node.Host, session.Node.Port, _stats.GetOrAdd(session.Node).NewPersistedNodeReputation)});
                }

                if (_logger.IsTrace) _logger.Trace($"ETH Protocol Initialized: {session.RemoteNodeId}");
            };
        }

        private bool RunBasicChecks(IP2PSession session, string protocolCode, int protocolVersion)
        {
            if (session.IsClosing)
            {
                if (_logger.IsTrace) _logger.Trace($"|NetworkTrace| Protocol initialized on closing session {protocolCode} {protocolVersion}, Node: {session.RemoteNodeId}");
                return true;
            }

            if (_logger.IsTrace) _logger.Trace($"|NetworkTrace| Protocol initialized {protocolCode} {protocolVersion}, Node: {session.RemoteNodeId}");
            return false;
        }

        /// <summary>
        /// In case of IN connection we don't know what is the port node is listening on until we receive the Hello message
        /// </summary>
        private void AddNodeToDiscovery(IP2PSession session, P2PProtocolInitializedEventArgs eventArgs)
        {
            if (eventArgs.ListenPort == 0)
            {
                if (_logger.IsTrace) _logger.Trace($"Listen port is 0, node is not listening: {session.Node.Id}, ConnectionType: {session.ConnectionDirection}, nodePort: {session.Node.Port}");
                return;
            }

            if (session.Node.Port != eventArgs.ListenPort)
            {
                if (_logger.IsDebug) _logger.Debug($"Updating listen port for node: {session.Node.Id}, ConnectionType: {session.ConnectionDirection}, from: {session.Node.Port} to: {eventArgs.ListenPort}");

                if (session.Node.AddedToDiscovery)
                {
                    if (_logger.IsDebug) _logger.Debug($"Discovery node already initialized with wrong port, nodeId: {session.Node.Id}, port: {session.Node.Port}, listen port: {eventArgs.ListenPort}");
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