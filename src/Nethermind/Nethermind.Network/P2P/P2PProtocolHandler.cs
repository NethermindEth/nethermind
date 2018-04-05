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
    public class P2PProtocolHandler : ProtocolHandlerBase, IProtocolHandler
    {
        private bool _sentHello;

        public P2PProtocolHandler(
            IP2PSession p2PSession,
            IMessageSerializationService serializer,
            PublicKey localNodeId,
            int listenPort,
            ILogger logger)
            : base(p2PSession, serializer, logger)
        {
            LocalNodeId = localNodeId;
            ListenPort = listenPort;
            AgreedCapabilities = new List<Capability>();
        }

        public List<Capability> AgreedCapabilities { get; }

        public int ListenPort { get; }

        public PublicKey LocalNodeId { get; }

        public string RemoteClientId { get; private set; }

        public void Init()
        {
            SendHello();
        }

        public void Close()
        {
        }

        public byte ProtocolVersion => 5;

        public string ProtocolCode => Protocol.P2P;

        public int MessageIdSpaceSize => 0x10;

        public void HandleMessage(Packet msg)
        {
            if (msg.PacketType == P2PMessageCode.Hello)
            {
                HandleHello(Deserialize<HelloMessage>(msg.Data));

                foreach (Capability capability in AgreedCapabilities.GroupBy(c => c.ProtocolCode).Select(c => c.OrderBy(v => v.Version).Last()))
                {
                    Logger.Log($"Starting protocolHandler for {capability.ProtocolCode} v{capability.Version} from {P2PSession.RemoteNodeId}:{P2PSession.RemotePort}");
                    SubprotocolRequested?.Invoke(this, new ProtocolEventArgs(capability.ProtocolCode, capability.Version));
                }
            }
            else if (msg.PacketType == P2PMessageCode.Disconnect)
            {
                DisconnectMessage disconnectMessage = Deserialize<DisconnectMessage>(msg.Data);
                Logger.Log($"Received disconnect ({disconnectMessage.Reason}) from {P2PSession.RemoteNodeId}:{P2PSession.RemotePort}");
                Close(disconnectMessage.Reason);
            }
            else if (msg.PacketType == P2PMessageCode.Ping)
            {
                Logger.Log($"Received PING from {P2PSession.RemoteNodeId}:{P2PSession.RemotePort}");
                HandlePing();
            }
            else if (msg.PacketType == P2PMessageCode.Pong)
            {
                Logger.Log($"Received PONG from {P2PSession.RemoteNodeId}:{P2PSession.RemotePort}");
                HandlePong();
            }
            else
            {
                Logger.Error($"Unhandled packet type: {msg.PacketType}");
            }
        }

        public void HandleHello(HelloMessage hello)
        {
            Logger.Log($"P2P received hello from {P2PSession.RemoteNodeId})");
            if (!hello.NodeId.Equals(P2PSession.RemoteNodeId))
            {
                throw new NodeDetailsMismatchException();
            }

            P2PSession.RemoteNodeId = hello.NodeId;
            P2PSession.RemotePort = hello.ListenPort;
            RemoteClientId = hello.ClientId;

            Logger.Log(!_sentHello
                ? $"P2P initiating inbound {hello.Protocol} v{hello.P2PVersion} protocolHandler from {hello.NodeId}:{hello.ListenPort} ({hello.ClientId})"
                : $"P2P initiating outbound {hello.Protocol} v{hello.P2PVersion} protocolHandler to {hello.NodeId}:{hello.ListenPort} ({hello.ClientId})");

            // https://github.com/ethereum/EIPs/blob/master/EIPS/eip-8.md
            // Clients implementing a newer version simply send a packet with higher version and possibly additional list elements.
            // * If such a packet is received by a node with lower version, it will blindly assume that the remote end is backwards-compatible and respond with the old handshake.
            // * If the packet is received by a node with equal version, new features of the protocol can be used.
            // * If the packet is received by a node with higher version, it can enable backwards-compatibility logic or drop the connection.
            if (hello.P2PVersion < ProtocolVersion)
            {
                Disconnect(DisconnectReason.IncompatibleP2PVersion);
                return;
            }

            foreach (Capability remotePeerCapability in hello.Capabilities)
            {
                if (SupportedCapabilities.Contains(remotePeerCapability))
                {
                    Logger.Log($"Agreed on {remotePeerCapability.ProtocolCode} v{remotePeerCapability.Version}");
                    AgreedCapabilities.Add(remotePeerCapability);
                }
                else
                {
                    Logger.Log($"Capability not supported {remotePeerCapability.ProtocolCode} v{remotePeerCapability.Version}");
                }
            }

            Debug.Assert(_sentHello, "Expecting Init to already be called at this point");
            ProtocolInitialized?.Invoke(this, EventArgs.Empty);
        }

        private static readonly List<Capability> SupportedCapabilities = new List<Capability>
        {
//            new Capability(Protocol.Eth, 62),
            new Capability(Protocol.Eth, 63),
        };

        private void SendHello()
        {
            Logger.Log($"P2P sending hello with Client ID {ClientVersion.Description}, protocol {ProtocolVersion}, listen port {ListenPort}");
            HelloMessage helloMessage = new HelloMessage
            {
                Capabilities = SupportedCapabilities.ToList(),
                ClientId = ClientVersion.Description,
                NodeId = LocalNodeId,
                ListenPort = ListenPort,
                P2PVersion = ProtocolVersion
            };

            _sentHello = true;
            Send(helloMessage);
        }

        private void HandlePing()
        {
            Logger.Log($"P2P responding to ping from {P2PSession.RemoteNodeId}:{P2PSession.RemotePort} ({RemoteClientId})");
            Send(PongMessage.Instance);
        }


        public void Disconnect(DisconnectReason disconnectReason)
        {
            // TODO: advertise disconnect up the stack so we actually disconnect   
            Logger.Log($"P2P disconnecting from {P2PSession.RemoteNodeId}:{P2PSession.RemotePort} ({RemoteClientId}) [{disconnectReason}]");
            DisconnectMessage message = new DisconnectMessage(disconnectReason);
            Send(message);
        }

        private void Close(DisconnectReason disconnectReason)
        {
            Logger.Log($"P2P received disconnect from {P2PSession.RemoteNodeId}:{P2PSession.RemotePort} ({RemoteClientId}) [{disconnectReason}]");
        }

        private void HandlePong()
        {
            Logger.Log($"P2P pong from {P2PSession.RemoteNodeId}:{P2PSession.RemotePort} ({RemoteClientId})");
        }

        private void Ping()
        {
            Logger.Log($"P2P sending ping to {P2PSession.RemoteNodeId}:{P2PSession.RemotePort} ({RemoteClientId})");
            // TODO: timers
            Send(PingMessage.Instance);
        }

        public event EventHandler ProtocolInitialized;
        public event EventHandler<ProtocolEventArgs> SubprotocolRequested;
    }
}