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
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Network.P2P.Subprotocols.Eth;
using Nethermind.Network.Rlpx;

namespace Nethermind.Network.P2P
{
    public class SessionManager : ISessionManager
    {
        private readonly int _listenPort;
        private readonly ILogger _logger;
        private readonly IMessageSerializationService _serializationService;
        private readonly PublicKey _localNodeId;

        private readonly Dictionary<int, ISession> _sessions = new Dictionary<int, ISession>();

        public SessionManager(IMessageSerializationService serializationService, PublicKey localNodeId, int listenPort, ILogger logger)
        {
            _serializationService = serializationService;
            _localNodeId = localNodeId;
            _listenPort = listenPort;
            _logger = logger;
        }

        // TODO: consider moving it out of here
        public void DeliverMessage(Packet packet)
        {
            if (!_sessions.ContainsKey(packet.ProtocolType))
            {
                throw new InvalidProtocolException(packet.ProtocolType);
            }

            _sessions[packet.ProtocolType].HandleMessage(packet);
        }

        public void Start(int protocolType, int version, IPacketSender packetSender, PublicKey remoteNodeId, int remotePort)
        {
            if (_sessions.ContainsKey(protocolType))
            {
                throw new InvalidOperationException($"Session for protocol {protocolType} already started");
            }

            ISession session;
            switch (protocolType)
            {
                case 0:
                    session = new P2PSession(this, _serializationService, packetSender, _localNodeId, _listenPort, remoteNodeId, _logger);
                    break;
                case 1:
                    session = new Eth62Session(_serializationService, packetSender, _logger, remoteNodeId, remotePort);
                    break;
                default:
                    throw new NotSupportedException();
            }

            _sessions[protocolType] = session;
            session.Init();
        }

        public void Close(int protocolType)
        {
            throw new NotImplementedException();
        }
    }
}