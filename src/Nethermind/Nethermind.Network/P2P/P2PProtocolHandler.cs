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
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.Network.Rlpx;
using Nethermind.Stats;
using Nethermind.Stats.Model;

namespace Nethermind.Network.P2P
{
    public class P2PProtocolHandler : ProtocolHandlerBase, IProtocolHandler, IPingSender
    {
        private readonly INodeStatsManager _nodeStatsManager;
        private readonly IPerfService _perfService;
        private bool _sentHello;
        private bool _isInitialized;
        private TaskCompletionSource<Packet> _pongCompletionSource;

        public P2PProtocolHandler(
            ISession session,
            PublicKey localNodeId,
            INodeStatsManager nodeStatsManager,
            IMessageSerializationService serializer,
            IPerfService perfService,
            ILogManager logManager)
            : base(session, nodeStatsManager, serializer, logManager)
        {
            _nodeStatsManager = nodeStatsManager ?? throw new ArgumentNullException(nameof(nodeStatsManager));
            _perfService = perfService ?? throw new ArgumentNullException(nameof(perfService));
            LocalNodeId = localNodeId;
            ListenPort = session.LocalPort;
            AgreedCapabilities = new List<Capability>();
            AvailableCapabilities = new List<Capability>();
        }

        public List<Capability> AgreedCapabilities { get; }
        public List<Capability> AvailableCapabilities { get; private set; }

        public int ListenPort { get; }

        public PublicKey LocalNodeId { get; }

        public string RemoteClientId { get; private set; }

        public bool HasAvailableCapability(Capability capability) => AvailableCapabilities.Contains(capability);
        public bool HasAgreedCapability(Capability capability) => AgreedCapabilities.Contains(capability);

        public void AddSupportedCapability(Capability capability)
        {
            if (SupportedCapabilities.Contains(capability))
            {
                return;
            }

            SupportedCapabilities.Add(capability);
        }

        public event EventHandler<ProtocolInitializedEventArgs> ProtocolInitialized;

        public event EventHandler<ProtocolEventArgs> SubprotocolRequested;
        
        public void Init()
        {
            SendHello();

            //We are expecting to receive Hello message anytime from the handshake completion, irrespective of sending Hello from our side
            CheckProtocolInitTimeout().ContinueWith(x =>
            {
                if (x.IsFaulted && Logger.IsError)
                {
                    Logger.Error("Error during p2pProtocol handler timeout logic", x.Exception);
                }
            }); 
        }

        public byte ProtocolVersion { get; private set; } = 5;

        public string ProtocolCode => Protocol.P2P;

        public int MessageIdSpaceSize => 0x10;

        public void HandleMessage(Packet msg)
        {
            if (msg.PacketType == P2PMessageCode.Hello)
            {
                HandleHello(Deserialize<HelloMessage>(msg.Data));
                Metrics.HellosReceived++;

                foreach (Capability capability in AgreedCapabilities.GroupBy(c => c.ProtocolCode).Select(c => c.OrderBy(v => v.Version).Last()))
                {
                    if(Logger.IsTrace) Logger.Trace($"{Session.RemoteNodeId} Starting protocolHandler for {capability.ProtocolCode} v{capability.Version} on {Session.RemotePort}");
                    SubprotocolRequested?.Invoke(this, new ProtocolEventArgs(capability.ProtocolCode, capability.Version));
                }
            }
            else if (msg.PacketType == P2PMessageCode.Disconnect)
            {
                DisconnectMessage disconnectMessage = Deserialize<DisconnectMessage>(msg.Data);
                if(Logger.IsTrace) Logger.Trace($"|NetworkTrace| {Session.RemoteNodeId} Received disconnect ({(Enum.IsDefined(typeof(DisconnectReason), (byte)disconnectMessage.Reason) ? ((DisconnectReason)disconnectMessage.Reason).ToString() : disconnectMessage.Reason.ToString())}) on {Session.RemotePort}");
                Close(disconnectMessage.Reason);
            }
            else if (msg.PacketType == P2PMessageCode.Ping)
            {
                if(Logger.IsTrace) Logger.Trace($"{Session.RemoteNodeId} Received PING on {Session.RemotePort}");
                HandlePing();
            }
            else if (msg.PacketType == P2PMessageCode.Pong)
            {
                if(Logger.IsTrace) Logger.Trace($"{Session.RemoteNodeId} Received PONG on {Session.RemotePort}");
                HandlePong(msg);
            }
            else if (msg.PacketType == P2PMessageCode.AddCapability)
            {
                var message = Deserialize<AddCapabilityMessage>(msg.Data);
                var capability = message.Capability;
                AgreedCapabilities.Add(message.Capability);
                SupportedCapabilities.Add(message.Capability);
                if(Logger.IsTrace) Logger.Trace($"{Session.RemoteNodeId} Starting protocolHandler for {capability.ProtocolCode} v{capability.Version} on {Session.RemotePort}");
                SubprotocolRequested?.Invoke(this, new ProtocolEventArgs(capability.ProtocolCode, capability.Version));
            }
            else
            {
                Logger.Error($"{Session.RemoteNodeId} Unhandled packet type: {msg.PacketType}");
            }
        }

        private void HandleHello(HelloMessage hello)
        {
            bool isInbound = !_sentHello;
            
            if(Logger.IsTrace) Logger.Trace($"{Session} P2P received hello.");     
            
            if (!hello.NodeId.Equals(Session.RemoteNodeId))
            {
                if(Logger.IsDebug) Logger.Debug($"Inconsistent Node ID details - expected {Session.RemoteNodeId}, received hello with {hello.NodeId} on " + (isInbound ? "IN connection" : "OUT connection"));
                // it does not really matter if there is mismatch - we do not use it anywhere
//                throw new NodeDetailsMismatchException();
            }

            RemoteClientId = hello.ClientId;
            Session.Node.ClientId = hello.ClientId;

            Logger.Trace(!_sentHello
                ? $"{Session.RemoteNodeId} P2P initiating inbound {hello.Protocol}.{hello.P2PVersion} on {hello.ListenPort} ({hello.ClientId})"
                : $"{Session.RemoteNodeId} P2P initiating outbound {hello.Protocol}.{hello.P2PVersion} on {hello.ListenPort} ({hello.ClientId})");

            // https://github.com/ethereum/EIPs/blob/master/EIPS/eip-8.md
            // Clients implementing a newer version simply send a packet with higher version and possibly additional list elements.
            // * If such a packet is received by a node with lower version, it will blindly assume that the remote end is backwards-compatible and respond with the old handshake.
            // * If the packet is received by a node with equal version, new features of the protocol can be used.
            // * If the packet is received by a node with higher version, it can enable backwards-compatibility logic or drop the connection.

            ProtocolVersion = hello.P2PVersion;

            var capabilities = hello.Capabilities;
            AvailableCapabilities = new List<Capability>(capabilities);
            foreach (Capability remotePeerCapability in capabilities)
            {
                if (SupportedCapabilities.Contains(remotePeerCapability))
                {
                    if(Logger.IsTrace) Logger.Trace($"{Session.RemoteNodeId} Agreed on {remotePeerCapability.ProtocolCode} v{remotePeerCapability.Version}");
                    AgreedCapabilities.Add(remotePeerCapability);
                }
                else
                {
                    if(Logger.IsTrace) Logger.Trace($"{Session.RemoteNodeId} Capability not supported {remotePeerCapability.ProtocolCode} v{remotePeerCapability.Version}");
                }
            }

            _isInitialized = true;

            if (!capabilities.Any(x => (x.ProtocolCode == Protocol.Eth && (x.Version == 62 || x.Version == 63))
                                       || x.ProtocolCode == Protocol.Ndm))
            {
                InitiateDisconnect(DisconnectReason.UselessPeer, $"capabilities: {string.Join(", ", capabilities.Select(c => string.Concat(c.ProtocolCode, c.Version)))}");
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
                if (Logger.IsWarn) Logger.Warn($"Another ping request in process: {Session.RemoteNodeId}");
                return true;
            }
            
            _pongCompletionSource = new TaskCompletionSource<Packet>();
            var pongTask = _pongCompletionSource.Task;

            if (Logger.IsTrace) Logger.Trace($"{Session} P2P sending ping on {Session.RemotePort} ({RemoteClientId})");
            Send(PingMessage.Instance);
            _nodeStatsManager.ReportEvent(Session.Node, NodeStatsEventType.P2PPingOut);
            var pingPerfCalcId = _perfService.StartPerfCalc(); 

            CancellationTokenSource delayCancellation = new CancellationTokenSource();
            var firstTask = await Task.WhenAny(pongTask, Task.Delay(Timeouts.P2PPing, delayCancellation.Token));
            _pongCompletionSource = null;
            if (firstTask != pongTask)
            {
                return false;
            }

            delayCancellation.Cancel();
            var latency = _perfService.EndPerfCalc(pingPerfCalcId);
            if (latency.HasValue)
            {
                _nodeStatsManager.ReportLatencyCaptureEvent(Session.Node, NodeLatencyStatType.P2PPingPong, latency.Value);
            }

            return true;
        }

        public void InitiateDisconnect(DisconnectReason disconnectReason, string details)
        {  
            if(Logger.IsTrace) Logger.Trace($"Sending disconnect {disconnectReason} ({details}) to {Session.Node:s}");
            DisconnectMessage message = new DisconnectMessage(disconnectReason);
            Send(message);
        }

        protected override TimeSpan InitTimeout => Timeouts.P2PHello;

        private static readonly List<Capability> SupportedCapabilities = new List<Capability>
        {
            new Capability(Protocol.Eth, 62),
            new Capability(Protocol.Eth, 63)
        };

        private void SendHello()
        {
            if (Logger.IsTrace)
            {
                Logger.Trace($"{Session} P2P.{ProtocolVersion} sending hello with Client ID {ClientVersion.Description}, protocol {ProtocolVersion}, listen port {ListenPort}");
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
            Metrics.HellosSent++;
        }

        private void HandlePing()
        {
            if (Logger.IsTrace) Logger.Trace($"{Session} P2P responding to ping");
            Send(PongMessage.Instance);
        }

        private void Close(int disconnectReasonId)
        {
            DisconnectReason disconnectReason = (DisconnectReason) disconnectReasonId;

            if (disconnectReason != DisconnectReason.TooManyPeers && disconnectReason != DisconnectReason.Other && disconnectReason != DisconnectReason.DisconnectRequested)
            {
                if (Logger.IsDebug) Logger.Debug($"{Session} received disconnect [{disconnectReason}]");
            }
            else
            {
                if (Logger.IsTrace) Logger.Trace($"{Session} P2P received disconnect [{disconnectReason}]");
            }

            // Received disconnect message, triggering direct TCP disconnection
            Session.Disconnect(disconnectReason, DisconnectType.Remote, "message");
        }

        private void HandlePong(Packet msg)
        {
            if(Logger.IsTrace) Logger.Trace($"{Session} sending P2P pong");
            _nodeStatsManager.ReportEvent(Session.Node, NodeStatsEventType.P2PPingIn);
            _pongCompletionSource?.TrySetResult(msg);
        }

        public void Dispose()
        {
        }
    }
}