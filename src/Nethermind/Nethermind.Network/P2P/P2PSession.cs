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
using System.Diagnostics;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Network.Rlpx;

namespace Nethermind.Network.P2P
{
    public class P2PSession : SessionBase, ISession
    {
        private readonly ILogger _logger;
        private bool _sentHello;

        // TODO: initialize with capabilities and version
        public P2PSession(
            IMessageSerializationService serializationService,
            IPacketSender packetSender,
            PublicKey localNodeId,
            int listenPort,
            PublicKey remoteNodeId,
            ILogger logger)
        :base(serializationService, packetSender, remoteNodeId, logger)
        {
            _logger = logger;
            LocalNodeId = localNodeId;
            ListenPort = listenPort;
            AgreedCapabilities = new List<Capability>();
        }

        public int ListenPort { get; }

        public List<Capability> AgreedCapabilities { get; }

        public PublicKey LocalNodeId { get; }

        public string RemoteClientId { get; private set; }

        public void Init() // TODO: remote node details here?
        {
            _logger.Log($"P2P initiating outbound session");
            SendHello();
        }

        public void Close()
        {
        }

        public int ProtocolVersion => 5;
        
        public string ProtocolCode => Protocol.P2P;

        public int MessageIdSpaceSize => 0x10;

        public void HandleMessage(Packet msg)
        {
            if (msg.PacketType == P2PMessageCode.Hello)
            {
                HelloMessage helloMessage = Deserialize<HelloMessage>(msg.Data);
                HandleHello(helloMessage);
                
                foreach (Capability capability in AgreedCapabilities.GroupBy(c => c.ProtocolCode).Select(c => c.OrderBy(v => v.Version).Last()))
                {    
                    _logger.Log($"Starting session for {capability.ProtocolCode} v{capability.Version} from {RemoteNodeId}:{RemotePort}");
                    SubprotocolRequested?.Invoke(this, new ProtocolEventArgs(capability.ProtocolCode, capability.Version));    
                } 
            }
            else if (msg.PacketType == P2PMessageCode.Disconnect)
            {
                DisconnectMessage disconnectMessage = Deserialize<DisconnectMessage>(msg.Data);
                _logger.Log($"Received disconnect ({disconnectMessage.Reason}) from {RemoteNodeId}:{RemotePort}");
                Close(disconnectMessage.Reason);
            }
            else if (msg.PacketType == P2PMessageCode.Ping)
            {
                _logger.Log($"Received PING from {RemoteNodeId}:{RemotePort}");
                HandlePing();
            }
            else if (msg.PacketType == P2PMessageCode.Pong)
            {
                _logger.Log($"Received PONG from {RemoteNodeId}:{RemotePort}");
                HandlePong();
            }
            else
            {
                _logger.Error($"Unhandled packet type: {msg.PacketType}");
            }
        }

        public void HandleHello(HelloMessage hello)
        {
            _logger.Log($"P2P received hello from {RemoteNodeId})");
            if (!hello.NodeId.Equals(RemoteNodeId))
            {
                throw new NodeDetailsMismatchException();
            }

            RemotePort = hello.ListenPort;
            RemoteClientId = hello.ClientId;

            _logger.Log(!_sentHello
                ? $"P2P initiating inbound {hello.Protocol} v{hello.P2PVersion} session from {hello.NodeId}:{hello.ListenPort} ({hello.ClientId})"
                : $"P2P initiating outbound {hello.Protocol} v{hello.P2PVersion} session to {hello.NodeId}:{hello.ListenPort} ({hello.ClientId})");

            // https://github.com/ethereum/EIPs/blob/master/EIPS/eip-8.md
            // Clients implementing a newer version simply send a packet with higher version and possibly additional list elements.
            // * If such a packet is received by a node with lower version, it will blindly assume that the remote end is backwards-compatible and respond with the old handshake.
            // * If the packet is received by a node with equal version, new features of the protocol can be used.
            // * If the packet is received by a node with higher version, it can enable backwards-compatibility logic or drop the connection.
            if (hello.P2PVersion < NettyP2PHandler.Version)
            {
                Close(DisconnectReason.IncompatibleP2PVersion);
                return;
            }

            if (hello.Capabilities.All(c => c.ProtocolCode != Protocol.Eth))
            {
                Close(DisconnectReason.Other);
                return;
            }

            if (hello.Capabilities.Where(c => c.ProtocolCode == Protocol.Eth).All(c => c.Version != 62))
            {
                Close(DisconnectReason.IncompatibleP2PVersion);
                return;
            }

            foreach (Capability remotePeerCapability in hello.Capabilities)
            {
                if (SupportedCapabilities.Contains(remotePeerCapability))
                {
                    _logger.Log($"Agreed on {remotePeerCapability.ProtocolCode} v{remotePeerCapability.Version}");
                    AgreedCapabilities.Add(remotePeerCapability);
                }
                else
                {
                    _logger.Log($"Capability not supported {remotePeerCapability.ProtocolCode} v{remotePeerCapability.Version}");
                }
            }
            
            Debug.Assert(_sentHello, "Expecting Init to already be called at this point");
            SessionEstablished?.Invoke(this, EventArgs.Empty);
        }
        
        private static readonly List<Capability> SupportedCapabilities = new List<Capability>
        {
            new Capability(Protocol.Eth, 62),
            new Capability(Protocol.Eth, 63),
        }; 
        
        private void SendHello()
        {
            _logger.Log($"P2P sending hello");
            HelloMessage helloMessage = new HelloMessage
            {
                Capabilities = new List<Capability>
                {
                    new Capability(Protocol.Eth, 62),
                    new Capability(Protocol.Eth, 63)
                },

                ClientId = ClientVersion.Description,
                NodeId = LocalNodeId,
                ListenPort = ListenPort,
                P2PVersion = (byte)ProtocolVersion
            };

            _sentHello = true;
            Send(helloMessage);
        }

        private void HandlePing()
        {
            _logger.Log($"P2P responding to ping from {RemoteNodeId}:{RemotePort} ({RemoteClientId})");
            Send(PongMessage.Instance);
        }

        
        public void Disconnect(DisconnectReason disconnectReason)
        {
            // TODO: advertise disconnect up the stack so we actually disconnect   
            _logger.Log($"P2P disconnecting from {RemoteNodeId}:{RemotePort} ({RemoteClientId}) [{disconnectReason}]");
            DisconnectMessage message = new DisconnectMessage(disconnectReason);
            Send(message);
        }

        private void Close(DisconnectReason disconnectReason)
        {
            _logger.Log($"P2P received disconnect from {RemoteNodeId}:{RemotePort} ({RemoteClientId}) [{disconnectReason}]");
        }

        private void HandlePong()
        {
            _logger.Log($"P2P pong from {RemoteNodeId}:{RemotePort} ({RemoteClientId})");
        }

        private void Ping()
        {
            _logger.Log($"P2P sending ping to {RemoteNodeId}:{RemotePort} ({RemoteClientId})");
            // TODO: timers
            Send(PingMessage.Instance);
        }

        public event EventHandler SessionEstablished;
        public event EventHandler<ProtocolEventArgs> SubprotocolRequested;
    }
}