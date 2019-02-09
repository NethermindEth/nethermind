using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Logging;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.Subprotocols.Eth;
using Nethermind.Network.P2P.Subprotocols.Eth.V63;
using Nethermind.Stats;

namespace Nethermind.Network
{
    public interface IProtocolsManager
    {
    }

    class ProtocolsManager : IProtocolsManager
    {
        private readonly IMessageSerializationService _serializationService;
        private readonly INodeStatsManager _nodeStatsManager;
        private readonly IPerfService _perfService;
        private readonly ILogManager _logManager;
        private readonly ILogger _logger;

        public ProtocolsManager(IMessageSerializationService serializationService, INodeStatsManager nodeStatsManager, IPerfService perfService, ILogManager logManager)
        {
            _serializationService = serializationService ?? throw new ArgumentNullException(nameof(serializationService));
            _nodeStatsManager = nodeStatsManager ?? throw new ArgumentNullException(nameof(nodeStatsManager));
            _perfService = perfService ?? throw new ArgumentNullException(nameof(perfService));
            _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
            _logger = _logManager.GetClassLogger();
        }
        
        private ConcurrentDictionary<Guid, HashSet<string>> _sessionProtocols = new ConcurrentDictionary<Guid, HashSet<string>>();

        private void InitProtocol(IP2PSession session, string protocolCode, int version)
        {
            if (session.SessionState < SessionState.Initialized)
            {
                throw new InvalidOperationException($"{nameof(InitProtocol)} called on session that is in the {SessionState} state");
            }

            if (session.SessionState != SessionState.Initialized)
            {
                return;
            }

            protocolCode = protocolCode.ToLowerInvariant();
            HashSet<string> _protocols = _sessionProtocols[session.SessionId];
            lock (_protocols)
            {
                if (_protocols.Contains(protocolCode))
                {
                    throw new InvalidOperationException($"{session.RemoteNodeId} Session for protocol {protocolCode} already started");
                }

                if (protocolCode != Protocol.P2P && !_protocols.Contains(Protocol.P2P))
                {
                    throw new InvalidOperationException($"{Protocol.P2P} protocolHandler has to be started before starting {protocolCode} protocolHandler");
                }

                IProtocolHandler protocolHandler;
                switch (protocolCode)
                {
                    case Protocol.P2P:
                        protocolHandler = new P2PProtocolHandler(this, _serializationService, session.Node.Id, session.Node.Port, _logManager, _perfService);
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

                            ProtocolInitialized?.Invoke(this, args);
                        };
                        break;
                    case Protocol.Eth:
                        if (version < 62 || version > 63)
                        {
                            throw new NotSupportedException($"Eth protocol version {version} is not supported.");
                        }

                        protocolHandler = version == 62
                            ? new Eth62ProtocolHandler(this, _serializer, _syncManager, _logManager, _perfService, _blockTree, _transactionPool, _timestamp)
                            : new Eth63ProtocolHandler(this, _serializer, _syncManager, _logManager, _perfService, _blockTree, _transactionPool, _timestamp);
                        protocolHandler.ProtocolInitialized += (sender, args) => { ProtocolInitialized?.Invoke(this, args); };
                        break;
                    default:
                        throw new NotSupportedException();
                }

                protocolHandler.SubprotocolRequested += (sender, args) => InitProtocol(args.ProtocolCode, args.Version);
                _protocols[protocolCode] = protocolHandler;

                (string ProtocolCode, int SpaceSize)[] alphabetically = new (string, int)[_protocols.Count];
                alphabetically[0] = (Protocol.P2P, _protocols[Protocol.P2P].MessageIdSpaceSize);
                int i = 1;
                foreach (KeyValuePair<string, IProtocolHandler> protocolSession in _protocols.Where(kv => kv.Key != "p2p").OrderBy(kv => kv.Key))
                {
                    alphabetically[i++] = (protocolSession.Key, protocolSession.Value.MessageIdSpaceSize);
                }

                _adaptiveCodeResolver = dynamicId =>
                {
                    int offset = 0;
                    for (int j = 0; j < alphabetically.Length; j++)
                    {
                        if (offset + alphabetically[j].SpaceSize > dynamicId)
                        {
                            return (alphabetically[j].ProtocolCode, dynamicId - offset);
                        }

                        offset += alphabetically[j].SpaceSize;
                    }

                    if (_logger.IsTrace) _logger.Warn($"Could not resolve message id from {dynamicId} with known: {string.Join(", ", alphabetically.Select(x => $"{x.ProtocolCode} {x.SpaceSize}"))}");

                    return (null, 0);
                };

                _adaptiveEncoder = args =>
                {
                    int offset = 0;
                    for (int j = 0; j < alphabetically.Length; j++)
                    {
                        if (alphabetically[j].ProtocolCode == args.ProtocolCode)
                        {
                            return offset + args.PacketType;
                        }

                        offset += alphabetically[j].SpaceSize;
                    }

                    return args.PacketType;
                };
            }

            protocolHandler.Init();
        }
        
                private void OnProtocolInitialized(object sender, ProtocolInitializedEventArgs e)
        {
            //Fire and forget
            Task.Run(async () => await OnProtocolInitializedAsync(sender, e));
        }

        private async Task OnProtocolInitializedAsync(object sender, ProtocolInitializedEventArgs e)
        {
            var session = (IP2PSession) sender;

            if (_logger.IsTrace) _logger.Trace($"|NetworkTrace| Protocol initialized {e.Subprotocol.ProtocolCode} {e.Subprotocol.ProtocolVersion}, Node: {session.RemoteNodeId}");

            if (!_activePeers.TryGetValue(session.RemoteNodeId, out var peer))
            {
                if (_candidatePeers.TryGetValue(session.RemoteNodeId, out var candidatePeer))
                {
                    if (e.Subprotocol is P2PProtocolHandler)
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

            switch (e.Subprotocol)
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
                    await _synchronizationManager.AddPeer(ethProtocolHandler);
                    _transactionPool.AddPeer(ethProtocolHandler);

                    break;
            }

            if (_logger.IsTrace) _logger.Trace($"Protocol Initialized: {session.RemoteNodeId}, {e.Subprotocol.GetType().Name}");
        }
    }
}