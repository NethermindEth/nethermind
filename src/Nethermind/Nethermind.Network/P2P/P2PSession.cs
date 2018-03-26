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

using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Network.Rlpx;

namespace Nethermind.Network.P2P
{
    public class P2PSession : ISession
    {
        private const int P2PVersion = 1;
        private readonly IMessageSender _messageSender;
        private readonly ILogger _logger;

        public P2PSession(IMessageSender messageSender, PublicKey localNodeId, int listenPort, ILogger logger)
        {
            _messageSender = messageSender;
            _logger = logger;
            LocalNodeId = localNodeId;
            ListenPort = listenPort;
        }
        
        public int ListenPort { get; }

        public PublicKey LocalNodeId { get; }
        
        public int RemoteListenPort { get; private set; }
        
        public PublicKey RemoteNodeId { get; private set; }

        public void InitInbound(HelloMessage hello)
        {
            if (hello.P2PVersion != NettyP2PHandler.Version)
            {
                Disconnect(DisconnectReason.IncompatibleP2PVersion);
                return;
            }
            
            RemoteNodeId = hello.NodeId;
            RemoteListenPort = hello.ListenPort;
            if (!hello.Capabilities.ContainsKey(Capability.Eth))
            {
                Disconnect(DisconnectReason.Other);
            }
        }

        public void InitOutbound()
        {
            HelloMessage helloMessage = new HelloMessage
            {
                Capabilities = new Dictionary<Capability, int>
                {
//                    {Capability.Eth, 0}
                },
                ClientId = ClientVersion.Description,
                NodeId = LocalNodeId,
                ListenPort = ListenPort,
                P2PVersion = P2PVersion
            };

            _messageSender.Enqueue(helloMessage);
        }

        public void HandlePing()
        {
            _messageSender.Enqueue(PongMessage.Instance);
        }

        public void Disconnect(DisconnectReason disconnectReason)
        {
            // TODO: advertise disconnect up the stack so we actually disconnect
            
            _logger.Log($"Disconnecting from ({RemoteNodeId}:{RemoteListenPort}) - useless peer");
            DisconnectMessage message = new DisconnectMessage(disconnectReason);
            _messageSender.Enqueue(message);
        }

        public void Close(DisconnectReason disconnectReason)
        {
        }

        public void HandlePong()
        {
        }

        public void Ping()
        {
            // TODO: timers
            _messageSender.Enqueue(PingMessage.Instance);
        }
    }
}