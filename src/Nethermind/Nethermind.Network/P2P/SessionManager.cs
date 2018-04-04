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
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Network.P2P.Subprotocols.Eth;
using Nethermind.Network.P2P.Subprotocols.Eth.V63;
using Nethermind.Network.Rlpx;

namespace Nethermind.Network.P2P
{   
    // TODO: this is close to ready to dynamically accept more protocols support but it seems unnecessary at the moment
    public class SessionManager : ISessionManager
    {
        private class SenderWrapper : IPacketSender
        {
            private readonly IPacketSender _sender;
            private readonly SessionManager _sessionManager;

            public SenderWrapper(IPacketSender sender, SessionManager sessionManager)
            {
                _sender = sender;
                _sessionManager = sessionManager;
            }
            
            public void Enqueue(Packet message, bool priority = false)
            {
                message.PacketType = _sessionManager._adaptiveEncoder((message.ProtocolType, message.PacketType));
                _sender.Enqueue(message, priority);
            }
        }
        
        private readonly int _listenPort;
        private readonly ILogger _logger;
        private readonly ISynchronizationManager _synchronizationManager;
        private readonly IMessageSerializationService _serializationService;
        private readonly PublicKey _localNodeId;

        private readonly Dictionary<string, ISession> _sessions = new Dictionary<string, ISession>();
        private Func<int, (string, int)> _adaptiveCodeResolver;
        private Func<(string ProtocolCode, int PacketType), int> _adaptiveEncoder;

        // TODO: this can only handle one remote peer at the moment, work in progress
        public SessionManager(
            IMessageSerializationService serializationService,
            PublicKey localNodeId,
            int listenPort,
            ILogger logger,
            ISynchronizationManager synchronizationManager) // TODO: review the class designs here
        {
            _serializationService = serializationService;
            _localNodeId = localNodeId;
            _listenPort = listenPort;
            _logger = logger;
            _synchronizationManager = synchronizationManager;
        }

        // TODO: move to a separate class?
        public (string, int) ResolveMessageCode(int adaptiveId)
        {
            return _adaptiveCodeResolver.Invoke(adaptiveId);
        }

        private IChannelController _channelController;
        
        public void RegisterChannelController(IChannelController channelController)
        {
            _channelController = channelController;
        }

        public void ReceiveMessage(Packet packet)
        {
            int dynamicMessageCode = packet.PacketType;
            (string protocol, int messageId) = ResolveMessageCode(dynamicMessageCode);
            packet.ProtocolType = protocol;
            
            _logger.Log($"Session Manager received a message (dynamic ID {dynamicMessageCode}. Resolved to {protocol}.{messageId})");

            if (protocol == null)
            {
                throw new InvalidProtocolException(packet.ProtocolType);
            }

            packet.PacketType = messageId;
            _sessions[protocol].HandleMessage(packet);
        }
        
        public void StartSession(string protocolCode, int version, IPacketSender packetSender, PublicKey remoteNodeId, int remotePort)
        {
            protocolCode = protocolCode.ToLowerInvariant();
            if (_sessions.ContainsKey(protocolCode))
            {
                throw new InvalidOperationException($"Session for protocol {protocolCode} already started");
            }

            if (protocolCode != Protocol.P2P && !_sessions.ContainsKey(Protocol.P2P))
            {
                throw new InvalidOperationException($"{Protocol.P2P} session has to be started before starting {protocolCode} session");
            }

            IPacketSender wrappedPacketSender = new SenderWrapper(packetSender, this);
            ISession session;
            switch (protocolCode)
            {
                case Protocol.P2P:
                    session = new P2PSession(_serializationService, new SenderWrapper(packetSender, this), _localNodeId, _listenPort, remoteNodeId, _logger);
                    session.SessionEstablished += (sender, args) =>
                    {
                        if (session.ProtocolVersion >= 5)
                        {
                            _logger.Log($"{session.ProtocolCode} v{session.ProtocolVersion} established - Enabling Snappy");
                            _channelController.EnableSnappy();
                        }
                    };
                    break;
                case Protocol.Eth:
                    if (version < 62 || version > 63)
                    {
                        throw new NotSupportedException();
                    }
                    
                    session = version == 62
                        ? new Eth62Session(_serializationService, packetSender, _logger, remoteNodeId, remotePort, _synchronizationManager)
                        : new Eth63Session(_serializationService, packetSender, _logger, remoteNodeId, remotePort, _synchronizationManager);
                    session.SessionEstablished += (sender, args) =>
                    {
                        ((P2PSession)_sessions[Protocol.P2P]).Disconnect(DisconnectReason.ClientQuitting);
                        _channelController.Disconnect(TimeSpan.FromSeconds(5));
                    }; 
                    break;
                default:
                    throw new NotSupportedException();
            }

            session.SubprotocolRequested += (sender, args) => StartSession(args.ProtocolCode, args.Version, wrappedPacketSender, remoteNodeId, remotePort);
            _sessions[protocolCode] = session;

            (string ProtocolCode, int SpaceSize)[] alphabetically = new (string, int)[_sessions.Count];
            alphabetically[0] = (Protocol.P2P, _sessions[Protocol.P2P].MessageIdSpaceSize);
            int i = 1;
            foreach (KeyValuePair<string, ISession> protocolSession in _sessions.Where(kv => kv.Key != "p2p").OrderBy(kv => kv.Key))
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

            session.Init();
        }
        
        public void CloseSession(string protocolCode)
        {
            protocolCode = protocolCode.ToLowerInvariant();
            throw new NotImplementedException();
        }
    }
}