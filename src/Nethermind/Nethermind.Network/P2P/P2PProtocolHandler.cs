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
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Logging;
using Nethermind.Core.Model;
using Nethermind.Network.Rlpx;
using Nethermind.Stats;
using Nethermind.Stats.Model;

namespace Nethermind.Network.P2P
{
    public class P2PProtocolHandler : ProtocolHandlerBase, IProtocolHandler, IP2PMessageSender
    {
        private bool _sentHello;
        private bool _isInitialized;
        private TaskCompletionSource<Packet> _pongCompletionSource;

        public P2PProtocolHandler(
            IP2PSession p2PSession,
            IMessageSerializationService serializer,
            NodeId localNodeId,
            int listenPort,
            ILogManager logManager, IPerfService perfService)
            : base(p2PSession, serializer, logManager, perfService)
        {
            LocalNodeId = localNodeId;
            ListenPort = listenPort;
            AgreedCapabilities = new List<Capability>();
        }

        public List<Capability> AgreedCapabilities { get; }

        public int ListenPort { get; }

        public NodeId LocalNodeId { get; }

        public string RemoteClientId { get; private set; }

        public event EventHandler<ProtocolInitializedEventArgs> ProtocolInitialized;

        public event EventHandler<ProtocolEventArgs> SubprotocolRequested;

        public void Init()
        {
            SendHello();

            //We are expecting receiving Hello message anytime from the handshake completion, irrespectful from sedning Hello from our side
            CheckProtocolInitTimeout().ContinueWith(x =>
            {
                if (x.IsFaulted && Logger.IsError)
                {
                    Logger.Error("Error during p2pProtocol handler timeout logic", x.Exception);
                }
            }); 
        }

        public void Close()
        {
        }

        public byte ProtocolVersion { get; private set; } = 5;

        public string ProtocolCode => Protocol.P2P;

        public int MessageIdSpaceSize => 0x10;

        public void HandleMessage(Packet msg)
        {
            if (msg.PacketType == P2PMessageCode.Hello)
            {
                HandleHello(Deserialize<HelloMessage>(msg.Data));

                foreach (Capability capability in AgreedCapabilities.GroupBy(c => c.ProtocolCode).Select(c => c.OrderBy(v => v.Version).Last()))
                {
                    if(Logger.IsTrace) Logger.Trace($"{P2PSession.RemoteNodeId} Starting protocolHandler for {capability.ProtocolCode} v{capability.Version} on {P2PSession.RemotePort}");
                    SubprotocolRequested?.Invoke(this, new ProtocolEventArgs(capability.ProtocolCode, capability.Version));
                }
            }
            else if (msg.PacketType == P2PMessageCode.Disconnect)
            {
                DisconnectMessage disconnectMessage = Deserialize<DisconnectMessage>(msg.Data);
                if(Logger.IsTrace) Logger.Trace($"|NetworkTrace| {P2PSession.RemoteNodeId} Received disconnect ({(Enum.IsDefined(typeof(DisconnectReason), (byte)disconnectMessage.Reason) ? ((DisconnectReason)disconnectMessage.Reason).ToString() : disconnectMessage.Reason.ToString())}) on {P2PSession.RemotePort}");
                Close(disconnectMessage.Reason);
            }
            else if (msg.PacketType == P2PMessageCode.Ping)
            {
                if(Logger.IsTrace) Logger.Trace($"{P2PSession.RemoteNodeId} Received PING on {P2PSession.RemotePort}");
                HandlePing();
            }
            else if (msg.PacketType == P2PMessageCode.Pong)
            {
                if(Logger.IsTrace) Logger.Trace($"{P2PSession.RemoteNodeId} Received PONG on {P2PSession.RemotePort}");
                HandlePong(msg);
            }
            else
            {
                Logger.Error($"{P2PSession.RemoteNodeId} Unhandled packet type: {msg.PacketType}");
            }
        }

        public void HandleHello(HelloMessage hello)
        {   
            if(Logger.IsTrace) Logger.Trace($"{P2PSession.RemoteNodeId} P2P received hello.");
            if (!hello.NodeId.Equals(P2PSession.RemoteNodeId))
            {
                Logger.Error("MISMATCH");
                throw new NodeDetailsMismatchException();
            }

            RemoteClientId = hello.ClientId;

            Logger.Trace(!_sentHello
                ? $"{P2PSession.RemoteNodeId} P2P initiating inbound {hello.Protocol} v{hello.P2PVersion} on {hello.ListenPort} ({hello.ClientId})"
                : $"{P2PSession.RemoteNodeId} P2P initiating outbound {hello.Protocol} v{hello.P2PVersion} on {hello.ListenPort} ({hello.ClientId})");

            // https://github.com/ethereum/EIPs/blob/master/EIPS/eip-8.md
            // Clients implementing a newer version simply send a packet with higher version and possibly additional list elements.
            // * If such a packet is received by a node with lower version, it will blindly assume that the remote end is backwards-compatible and respond with the old handshake.
            // * If the packet is received by a node with equal version, new features of the protocol can be used.
            // * If the packet is received by a node with higher version, it can enable backwards-compatibility logic or drop the connection.

            ProtocolVersion = hello.P2PVersion;

            //TODO Check required capabilities and disconnect if not supported

            var capabilities = hello.Capabilities;
            foreach (Capability remotePeerCapability in capabilities)
            {
                if (SupportedCapabilities.Contains(remotePeerCapability))
                {
                    if(Logger.IsTrace) Logger.Trace($"{P2PSession.RemoteNodeId} Agreed on {remotePeerCapability.ProtocolCode} v{remotePeerCapability.Version}");
                    AgreedCapabilities.Add(remotePeerCapability);
                }
                else
                {
                    if(Logger.IsTrace) Logger.Trace($"{P2PSession.RemoteNodeId} Capability not supported {remotePeerCapability.ProtocolCode} v{remotePeerCapability.Version}");
                }
            }

            _isInitialized = true;
            
            // TODO: proper validation on connection
            if(!capabilities.Any(x => x.ProtocolCode == Protocol.Eth && x.Version == 62))
            {    
                Disconnect(DisconnectReason.UselessPeer);
            }
                
            ReceivedProtocolInitMsg(hello);

            var eventArgs = new P2PProtocolInitializedEventArgs(this)
            {
                P2PVersion = ProtocolVersion,
                ClientId = RemoteClientId,
                Capabilities = capabilities,
                ListenPort = hello.ListenPort
            };
            ProtocolInitialized?.Invoke(this, eventArgs);
        }

        public async Task<bool> SendPing()
        {
            if (!_isInitialized)
            {
                return true;
            }
            
            if (_pongCompletionSource != null)
            {
                if (Logger.IsWarn) Logger.Warn($"Another ping request in process: {P2PSession.RemoteNodeId}");
                return true;
            }
            
            _pongCompletionSource = new TaskCompletionSource<Packet>();
            var pongTask = _pongCompletionSource.Task;

            if (Logger.IsTrace) Logger.Trace($"{P2PSession.RemoteNodeId} P2P sending ping on {P2PSession.RemotePort} ({RemoteClientId})");
            Send(PingMessage.Instance);
            P2PSession?.NodeStats.AddNodeStatsEvent(NodeStatsEventType.P2PPingOut);
            var pingPerfCalcId = PerfService.StartPerfCalc(); 

            var firstTask = await Task.WhenAny(pongTask, Task.Delay(Timeouts.P2PPing));
            _pongCompletionSource = null;
            if (firstTask != pongTask)
            {
                return false;
            }

            var latency = PerfService.EndPerfCalc(pingPerfCalcId);
            if (latency.HasValue)
            {
                P2PSession?.NodeStats.AddLatencyCaptureEvent(NodeLatencyStatType.P2PPingPong, latency.Value);
            }

            return true;
        }

        public void Disconnect(DisconnectReason disconnectReason)
        {  
            if(Logger.IsTrace) Logger.Trace($"{P2PSession.RemoteNodeId} P2P disconnecting on {P2PSession.RemotePort} ({RemoteClientId}) [{disconnectReason}]");
            DisconnectMessage message = new DisconnectMessage(disconnectReason);
            Send(message);
        }

        protected override TimeSpan InitTimeout => Timeouts.P2PHello;

        private static readonly List<Capability> SupportedCapabilities = new List<Capability>
        {
            new Capability(Protocol.Eth, 62),
        };

        private void SendHello()
        {
            if (Logger.IsTrace)
            {
                Logger.Trace($"{P2PSession.RemoteNodeId} P2P sending hello with Client ID {ClientVersion.Description}, protocol {ProtocolVersion}, listen port {ListenPort}");
            }

            var helloMessage = new HelloMessage
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
            if (Logger.IsTrace) Logger.Trace($"{P2PSession.RemoteNodeId} P2P responding to ping on {P2PSession.RemotePort} ({RemoteClientId})");
            Send(PongMessage.Instance);
        }

        private void Close(int disconnectReason)
        {
            if(Logger.IsTrace) Logger.Trace($"{P2PSession.RemoteNodeId} P2P received disconnect on {P2PSession.RemotePort} ({RemoteClientId}) [{disconnectReason}]");
            // Received disconnect message, triggering direct TCP disconnection
            P2PSession.DisconnectAsync((DisconnectReason) disconnectReason, DisconnectType.Remote);
        }

        private void HandlePong(Packet msg)
        {
            if(Logger.IsTrace) Logger.Trace($"{P2PSession.RemoteNodeId} P2P pong on {P2PSession.RemotePort} ({RemoteClientId})");
            P2PSession?.NodeStats.AddNodeStatsEvent(NodeStatsEventType.P2PPingIn);
            _pongCompletionSource?.TrySetResult(msg);
        }
    }
}