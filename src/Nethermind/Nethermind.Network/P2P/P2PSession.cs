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
        private readonly IMessageSender _messageSender;
        private readonly ILogger _logger;
        private bool _sentHello;

        // TODO: initialize with capabilities and version
        public P2PSession(IMessageSender messageSender, PublicKey localNodeId, int listenPort, ILogger logger)
        {
            _messageSender = messageSender;
            _logger = logger;
            LocalNodeId = localNodeId;
            ListenPort = listenPort;
            AgreedCapabilities = new Dictionary<Capability, int>();
        }
        
        public int ListenPort { get; }

        public Dictionary<Capability, int> AgreedCapabilities { get; }
        
        public PublicKey LocalNodeId { get; }
        
        public int RemoteListenPort { get; private set; }
        
        public PublicKey RemoteNodeId { get; private set; }
        
        public string RemoteClientId { get; private set; }

        public void HandleHello(HelloMessage hello)
        {
            _logger.Log($"P2P received hello from {RemoteNodeId}:{RemoteListenPort} ({RemoteClientId})");
            RemoteNodeId = hello.NodeId;
            RemoteListenPort = hello.ListenPort;
            RemoteClientId = hello.ClientId;
            
            if (!_sentHello)
            {   
                _logger.Log($"P2P initiating inbound session from {hello.NodeId}:{hello.ListenPort} ({hello.ClientId})");       
            }
            
            // TODO: temp
            if (hello.P2PVersion != NettyP2PHandler.Version)
            {
                Disconnect(DisconnectReason.IncompatibleP2PVersion);
                return;
            }
            
            if (!hello.Capabilities.ContainsKey(Capability.Eth))
            {
                Disconnect(DisconnectReason.Other);
            }

            if (hello.Capabilities[Capability.Eth] != 62)
            {
                Disconnect(DisconnectReason.Other);
            }
                
            AgreedCapabilities.Add(Capability.Eth, 62);
        }

        public void InitOutbound() // TODO: remote node details here?
        {
            _logger.Log($"P2P initiating outbound session");
            SendHello();
        }

        private void SendHello()
        {
            _logger.Log($"P2P sending hello");
            HelloMessage helloMessage = new HelloMessage
            {
                Capabilities = new Dictionary<Capability, int>
                {
//                    {Capability.Eth, 0}
                },
                ClientId = ClientVersion.Description,
                NodeId = LocalNodeId,
                ListenPort = ListenPort,
                P2PVersion = NettyP2PHandler.Version
            };

            _sentHello = true;
            _messageSender.Enqueue(helloMessage);
        }

        public void HandlePing()
        {
            _logger.Log($"P2P responding to ping from {RemoteNodeId}:{RemoteListenPort} ({RemoteClientId})");
            _messageSender.Enqueue(PongMessage.Instance);
        }

        public void Disconnect(DisconnectReason disconnectReason)
        {
            // TODO: advertise disconnect up the stack so we actually disconnect   
            _logger.Log($"P2P disconnecting from {RemoteNodeId}:{RemoteListenPort} ({RemoteClientId}) [{disconnectReason}]");
            DisconnectMessage message = new DisconnectMessage(disconnectReason);
            _messageSender.Enqueue(message);
        }

        public void Close(DisconnectReason disconnectReason)
        {
            _logger.Log($"P2P received disconnect from {RemoteNodeId}:{RemoteListenPort} ({RemoteClientId}) [{disconnectReason}]");
        }

        public void HandlePong()
        {
            _logger.Log($"P2P pong from {RemoteNodeId}:{RemoteListenPort} ({RemoteClientId})");
        }

        public void Ping()
        {
            _logger.Log($"P2P sending ping to {RemoteNodeId}:{RemoteListenPort} ({RemoteClientId})");
            // TODO: timers
            _messageSender.Enqueue(PingMessage.Instance);
        }
    }
}