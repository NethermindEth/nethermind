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
        private readonly INodeStatsManager _nodeStatsManager;
        private readonly IPerfService _perfService;
        private readonly ILogManager _logManager;
        private readonly ITimestamp _timestamp = new Timestamp();
        private readonly ILogger _logger;

        public ProtocolsManager(IRlpxPeer localPeer, ISynchronizationManager synchronizationManager, IBlockTree blockTree, ITransactionPool transactionPool, IDiscoveryApp discoveryApp, IMessageSerializationService serializationService, INodeStatsManager nodeStatsManager, IPerfService perfService, ILogManager logManager)
        {
            _syncManager = synchronizationManager ?? throw new ArgumentNullException(nameof(synchronizationManager));
            _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
            _transactionPool = transactionPool ?? throw new ArgumentNullException(nameof(transactionPool));
            _discoveryApp = discoveryApp ?? throw new ArgumentNullException(nameof(discoveryApp));
            _serializer = serializationService ?? throw new ArgumentNullException(nameof(serializationService));
            _nodeStatsManager = nodeStatsManager ?? throw new ArgumentNullException(nameof(nodeStatsManager));
            _perfService = perfService ?? throw new ArgumentNullException(nameof(perfService));
            _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
            _logger = _logManager.GetClassLogger();

            localPeer.SessionCreated += SessionCreated;
        }

        private void SessionCreated(object sender, SessionEventArgs e)
        {
            e.Session.Initialized += SessionInitialized;
            e.Session.Disconnected += SessionDisconnected;
        }

        private void SessionDisconnected(object sender, DisconnectEventArgs e)
        {
            IP2PSession session = (IP2PSession) sender;
            session.Initialized -= SessionInitialized;
            session.Disconnected -= SessionDisconnected;
        }

        private void SessionInitialized(object sender, EventArgs e)
        {
            IP2PSession session = (IP2PSession) sender;
            InitProtocol(session, Protocol.P2P, session.P2PVersion);
        }

        private async Task<bool> ValidateProtocol(string protocol, IP2PSession session, ProtocolInitializedEventArgs eventArgs)
        {
            switch (protocol)
            {
                case Protocol.P2P:
                    var args = (P2PProtocolInitializedEventArgs) eventArgs;
                    if (!ValidateP2PVersion(args.P2PVersion))
                    {
                        if (_logger.IsTrace) _logger.Trace($"Initiating disconnect with peer: {session.RemoteNodeId}, incorrect P2PVersion: {args.P2PVersion}");
                        _nodeStatsManager.ReportFailedValidation(session.Node, CompatibilityValidationType.P2PVersion);
                        await session.InitiateDisconnectAsync(DisconnectReason.IncompatibleP2PVersion);
                        return false;
                    }

                    if (!ValidateCapabilities(args.Capabilities))
                    {
                        if (_logger.IsTrace) _logger.Trace($"Initiating disconnect with peer: {session.RemoteNodeId}, no Eth62 capability, supported capabilities: [{string.Join(",", args.Capabilities.Select(x => $"{x.ProtocolCode}v{x.Version}"))}]");
                        _nodeStatsManager.ReportFailedValidation(session.Node, CompatibilityValidationType.Capabilities);
                        await session.InitiateDisconnectAsync(DisconnectReason.UselessPeer);
                        return false;
                    }

                    break;
                case Protocol.Eth:
                    var ethArgs = (EthProtocolInitializedEventArgs) eventArgs;
                    if (!ValidateChainId(ethArgs.ChainId))
                    {
                        if (_logger.IsTrace) _logger.Trace($"Initiating disconnect with peer: {session.RemoteNodeId}, different chainId: {ChainId.GetChainName((int) ethArgs.ChainId)}, our chainId: {ChainId.GetChainName(_syncManager.ChainId)}");

                        _nodeStatsManager.ReportFailedValidation(session.Node, CompatibilityValidationType.ChainId);
                        await session.InitiateDisconnectAsync(DisconnectReason.UselessPeer);
                        return false;
                    }

                    if (ethArgs.GenesisHash != _syncManager.Genesis?.Hash)
                    {
                        if (_logger.IsTrace) _logger.Trace($"Initiating disconnect with peer: {session.RemoteNodeId}, different genesis hash: {ethArgs.GenesisHash}, our: {_syncManager.Genesis?.Hash}");

                        _nodeStatsManager.ReportFailedValidation(session.Node, CompatibilityValidationType.DifferentGenesis);
                        await session.InitiateDisconnectAsync(DisconnectReason.BreachOfProtocol);
                        return false;
                    }

                    break;
            }

            return true;
        }

        private bool ValidateP2PVersion(byte p2PVersion)
        {
            return p2PVersion == 4 || p2PVersion == 5;
        }

        private bool ValidateCapabilities(IEnumerable<Capability> capabilities)
        {
            return capabilities.Any(x => x.ProtocolCode == Protocol.Eth && (x.Version == 62 || x.Version == 63));
        }

        private bool ValidateChainId(long chainId)
        {
            return chainId == _syncManager.ChainId;
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
                    protocolHandler = new P2PProtocolHandler(session, _nodeStatsManager, _serializer, _perfService, _logManager);
                    protocolHandler.ProtocolInitialized += (sender, args) =>
                    {
                        if (protocolHandler.ProtocolVersion >= 5)
                        {
                            if (_logger.IsTrace) _logger.Trace($"{session.RemoteNodeId} {protocolHandler.ProtocolCode} v{protocolHandler.ProtocolVersion} established - Enabling Snappy");
                            session.EnableSnappy();
                        }
                        else
                        {
                            if (_logger.IsTrace) _logger.Trace($"{session.RemoteNodeId} {protocolHandler.ProtocolCode} v{protocolHandler.ProtocolVersion} established - Disabling Snappy");
                        }

                        OnProtocolInitialized(session, args);
                    };
                    break;
                case Protocol.Eth:
                    if (version < 62 || version > 63)
                    {
                        throw new NotSupportedException($"Eth protocol version {version} is not supported.");
                    }

                    protocolHandler = version == 62
                        ? new Eth62ProtocolHandler(session, _serializer, _syncManager, _logManager, _perfService, _blockTree, _transactionPool, _timestamp)
                        : new Eth63ProtocolHandler(session, _serializer, _syncManager, _logManager, _perfService, _blockTree, _transactionPool, _timestamp);
                    protocolHandler.ProtocolInitialized += (sender, args) => { OnProtocolInitialized(session, protocolHandler); };
                    break;
                default:
                    throw new NotSupportedException();
            }

            protocolHandler.SubprotocolRequested += (sender, args) => InitProtocol(session, args.ProtocolCode, args.Version);
            session.AddProtocolHandler(protocolHandler);

            

            protocolHandler.Init();
        }

        private void OnProtocolInitialized(IP2PSession session, P2PProtocolInitializedEventArgs protocolHandler)
        {
            //Fire and forget
            Task.Run(async () => await OnProtocolInitializedAsync(session, protocolHandler));
        }

        private async Task OnProtocolInitializedAsync(IP2PSession session, P2PProtocolInitializedEventArgs protocolHandler)
        {
            if (session.IsClosing)
            {
                if (_logger.IsTrace) _logger.Trace($"|NetworkTrace| Protocol initialized on closing session {protocolHandler.ProtocolCode} {protocolHandler.ProtocolVersion}, Node: {session.RemoteNodeId}");
                return;
            }

            if (_logger.IsTrace) _logger.Trace($"|NetworkTrace| Protocol initialized {protocolHandler.ProtocolCode} {protocolHandler.ProtocolVersion}, Node: {session.RemoteNodeId}");

            if (!_activePeers.TryGetValue(session.RemoteNodeId, out var peer))
            {
                if (_candidatePeers.TryGetValue(session.RemoteNodeId, out var candidatePeer))
                {
                    if (protocolHandler is P2PProtocolHandler)
                    {
                        AddNodeToDiscovery(candidatePeer, (P2PProtocolInitializedEventArgs) e);
                    }

                    if (_logger.IsError) _logger.Error($"Protocol {e.Subprotocol.ProtocolCode} initialized for peer not present in active collection, id: {session.RemoteNodeId}.");
                }
                else
                {
                    if (_logger.IsError) _logger.Error($"Protocol {e.Subprotocol.ProtocolCode} initialized for peer not present in active collection, id: {session.RemoteNodeId}, peer not in candidate collection.");
                }

                //Initializing disconnect if it hasn't been done already - in case of e.g. timeout earlier and unexpected further connection
                await session.InitiateDisconnectAsync(DisconnectReason.Other);

                return;
            }

            switch (protocolHandler)
            {
                case P2PProtocolHandler p2PProtocolHandler:
                    var p2PEventArgs = (P2PProtocolInitializedEventArgs) e;
                    AddNodeToDiscovery(peer, p2PEventArgs);
                    _nodeStatsManager.ReportP2PInitializationEvent(session.Node, new P2PNodeDetails
                    {
                        ClientId = p2PEventArgs.ClientId,
                        Capabilities = p2PEventArgs.Capabilities.ToArray(),
                        P2PVersion = p2PEventArgs.P2PVersion,
                        ListenPort = p2PEventArgs.ListenPort
                    });

                    var result = await ValidateProtocol(Protocol.P2P, session, e);
                    if (!result)
                    {
                        return;
                    }

                    session.P2PMessageSender = p2PProtocolHandler;
                    break;
                case Eth62ProtocolHandler ethProtocolHandler: // note that this covers eth63 as well
                    var ethEventArgs = (EthProtocolInitializedEventArgs) e;
                    _nodeStatsManager.ReportEthInitializeEvent(session.Node, new EthNodeDetails
                    {
                        ChainId = ethEventArgs.ChainId,
                        BestHash = ethEventArgs.BestHash,
                        GenesisHash = ethEventArgs.GenesisHash,
                        Protocol = ethEventArgs.Protocol,
                        ProtocolVersion = ethEventArgs.ProtocolVersion,
                        TotalDifficulty = ethEventArgs.TotalDifficulty
                    });
                    result = await ValidateProtocol(Protocol.Eth, session, e);
                    if (!result)
                    {
                        return;
                    }

                    //TODO move this outside, so syncManager have access to NodeStats and NodeDetails
                    ethProtocolHandler.ClientId = peer.NodeStats.P2PNodeDetails.ClientId;
                    peer.SynchronizationPeer = ethProtocolHandler;

                    if (_logger.IsTrace) _logger.Trace($"Eth version {ethProtocolHandler.ProtocolVersion} initialized, adding sync peer: {peer.Node.Id}");

                    //Add/Update peer to the storage and to sync manager
                    _peerStorage.UpdateNodes(new[] {new NetworkNode(peer.Node.Id, peer.Node.Host, peer.Node.Port, peer.NodeStats.NewPersistedNodeReputation)});
                    await _syncManager.AddPeer(ethProtocolHandler);
                    _transactionPool.AddPeer(ethProtocolHandler);

                    break;
            }

            if (_logger.IsTrace) _logger.Trace($"Protocol Initialized: {session.RemoteNodeId}, {e.Subprotocol.GetType().Name}");
        }

        /// <summary>
        /// In case of IN connection we don't know what is the port node is listening on until we receive the Hello message
        /// </summary>
        private void AddNodeToDiscovery(Peer peer, P2PProtocolInitializedEventArgs eventArgs)
        {
            if (eventArgs.ListenPort == 0)
            {
                if (_logger.IsTrace) _logger.Trace($"Listen port is 0, node is not listening: {peer.Node.Id}, ConnectionType: {(peer.OutSession ?? peer.InSession).ConnectionDirection}, nodePort: {peer.Node.Port}");
                return;
            }

            if (peer.Node.Port != eventArgs.ListenPort)
            {
                if (_logger.IsDebug) _logger.Debug($"Updating listen port for node: {peer.Node.Id}, ConnectionType: {(peer.OutSession ?? peer.InSession).ConnectionDirection}, from: {peer.Node.Port} to: {eventArgs.ListenPort}");

                if (peer.AddedToDiscovery)
                {
                    if (_logger.IsDebug) _logger.Debug($"Discovery node already initialized with wrong port, nodeId: {peer.Node.Id}, port: {peer.Node.Port}, listen port: {eventArgs.ListenPort}");
                }

                peer.Node.Port = eventArgs.ListenPort;
            }

            AddNodeToDiscovery(peer);
        }

        private void AddNodeToDiscovery(Peer peer)
        {
            if (peer.AddedToDiscovery)
            {
                return;
            }

            //In case peer was initiated outside of discovery and discovery is enabled, we are adding it to discovery for future use (e.g. trusted peer)
            _discoveryApp.AddNodeToDiscovery(peer.Node);
            peer.AddedToDiscovery = true;
        }
    }
}