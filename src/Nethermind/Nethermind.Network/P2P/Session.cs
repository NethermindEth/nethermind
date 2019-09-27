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
using System.Linq;
using System.Threading.Tasks;
using DotNetty.Transport.Channels;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Logging;
using Nethermind.Network.Rlpx;
using Nethermind.Stats.Model;

namespace Nethermind.Network.P2P
{
    public class Session : ISession
    {
        private static ConcurrentDictionary<string, AdaptiveCodeResolver> _resolvers = new ConcurrentDictionary<string, AdaptiveCodeResolver>();
        
        private ILogger _logger;
        private ILogManager _logManager;
        
        private Node _node;
        private IChannel _channel;
        private IChannelHandlerContext _context;
        
        private Dictionary<string, IProtocolHandler> _protocols = new Dictionary<string, IProtocolHandler>();

        public Session(int localPort, ILogManager logManager, IChannel channel)
        {
            Direction = ConnectionDirection.In;
            State = SessionState.New;
            _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
            _channel = channel ?? throw new ArgumentNullException(nameof(channel));
            _logger = logManager.GetClassLogger<Session>();
            RemoteNodeId = null;
            LocalPort = localPort;
            SessionId = Guid.NewGuid();
        }

        public Session(
            int localPort,
            ILogManager logManager,
            IChannel channel,
            Node node)
        {
            State = SessionState.New;
            _node = node ?? throw new ArgumentNullException(nameof(node));
            _channel = channel ?? throw new ArgumentNullException(nameof(channel));
            _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
            _logger = logManager.GetClassLogger<Session>();
            RemoteNodeId = node.Id;
            RemoteHost = node.Host;
            RemotePort = node.Port;
            LocalPort = localPort;
            SessionId = Guid.NewGuid();
            Direction = ConnectionDirection.Out;
        }

        public bool IsClosing => State > SessionState.Initialized;
        public int LocalPort { get; set; }
        public PublicKey RemoteNodeId { get; set; }
        public PublicKey ObsoleteRemoteNodeId { get; set; }
        public string RemoteHost { get; set; }
        public int RemotePort { get; set; }
        public DateTime LastPingUtc { get; set; } = DateTime.UtcNow;
        public DateTime LastPongUtc { get; set; } = DateTime.UtcNow;
        public ConnectionDirection Direction { get; }
        public Guid SessionId { get; }

        public Node Node
        {
            get
            {
                //It is needed for lazy creation of Node, in case  IN connections, publicKey is available only after handshake
                if (_node == null)
                {
                    if (RemoteNodeId == null || RemoteHost == null || RemotePort == 0)
                    {
                        throw new InvalidOperationException("Cannot create a session's node object without knowing remote node details");
                    }

                    _node = new Node(RemoteNodeId, RemoteHost, RemotePort);
                }

                return _node;
            }
            set => _node = value;
        }

        public void EnableSnappy()
        {
            lock (_sessionStateLock)
            {
                if (State < SessionState.Initialized)
                {
                    throw new InvalidOperationException($"{nameof(EnableSnappy)} called on {this}");
                }

                if (IsClosing)
                {
                    return;
                }
            }

            if (_logger.IsTrace) _logger.Trace($"Enabling Snappy compression and disabling framing in {this}");
            _context.Channel.Pipeline.Get<ZeroPacketSplitter>()?.DisableFraming();
            
            // since groups were used, we are on a different thread
            _context.Channel.Pipeline.Get<ZeroNettyP2PHandler>()?.EnableSnappy();
            // code in the next line does no longer work as if there is a packet waiting then it will skip the snappy decoder
            // _context.Channel.Pipeline.AddBefore($"{nameof(PacketSender)}#0", null, new SnappyDecoder(_logger));
            _context.Channel.Pipeline.AddBefore($"{nameof(PacketSender)}#0", null, new ZeroSnappyEncoder(_logManager));
        }

        public void AddSupportedCapability(Capability capability)
        {
            if (!_protocols.TryGetValue(Protocol.P2P, out var protocol))
            {
                return;
            }

            protocol.AddSupportedCapability(capability);
        }

        public bool HasAvailableCapability(Capability capability)
            => _protocols.TryGetValue(Protocol.P2P, out var protocol) && protocol.HasAvailableCapability(capability);

        public bool HasAgreedCapability(Capability capability)
            => _protocols.TryGetValue(Protocol.P2P, out var protocol) && protocol.HasAgreedCapability(capability);

        public IPingSender PingSender { get; set; }

        public void ReceiveMessage(ZeroPacket zeroPacket)
        {
            lock (_sessionStateLock)
            {
                if (State < SessionState.Initialized)
                {
                    throw new InvalidOperationException($"{nameof(ReceiveMessage)} called on {this}");
                }

                if (IsClosing)
                {
                    return;
                }
            }

            int dynamicMessageCode = zeroPacket.PacketType;
            (string protocol, int messageId) = _resolver.ResolveProtocol(zeroPacket.PacketType);
            zeroPacket.Protocol = protocol;

            if (_logger.IsTrace) _logger.Trace($"{this} received a message of length {zeroPacket.Content.ReadableBytes} ({dynamicMessageCode} => {protocol}.{messageId})");

            if (protocol == null)
            {
                if (_logger.IsTrace) _logger.Warn($"Received a message from node: {RemoteNodeId}, ({dynamicMessageCode} => {messageId}), known protocols ({_protocols.Count}): {string.Join(", ", _protocols.Select(x => $"{x.Key}.{x.Value.ProtocolVersion} {x.Value.MessageIdSpaceSize}"))}");
                return;
            }

            zeroPacket.PacketType = (byte)messageId;
            IProtocolHandler protocolHandler = _protocols[protocol];
            IZeroProtocolHandler zeroProtocolHandler = protocolHandler as IZeroProtocolHandler;
            if (zeroProtocolHandler != null)
            {
                zeroProtocolHandler.HandleMessage(zeroPacket);
            }
            else
            {
                protocolHandler.HandleMessage(new Packet(zeroPacket));
            }
        }

        public void DeliverMessage<T>(T message) where T : P2PMessage
        {
            lock (_sessionStateLock)
            {
                if (State < SessionState.Initialized)
                {
                    throw new InvalidOperationException($"{nameof(DeliverMessage)} called {this}");
                }

                if (IsClosing)
                {
                    return;
                }
            }

            if (_logger.IsTrace) _logger.Trace($"P2P to deliver {message.Protocol}.{message.PacketType} on {this}");

            message.AdaptivePacketType = _resolver.ResolveAdaptiveId(message.Protocol, message.PacketType);
            _packetSender.Enqueue(message);
        }

        public void ReceiveMessage(Packet packet)
        {
            lock (_sessionStateLock)
            {
                if (State < SessionState.Initialized)
                {
                    throw new InvalidOperationException($"{nameof(ReceiveMessage)} called on {this}");
                }

                if (IsClosing)
                {
                    return;
                }
            }

            int dynamicMessageCode = packet.PacketType;
            (string protocol, int messageId) = _resolver.ResolveProtocol(packet.PacketType);
            packet.Protocol = protocol;

            if (_logger.IsTrace) _logger.Trace($"{this} received a message of length {packet.Data.Length} ({dynamicMessageCode} => {protocol}.{messageId})");

            if (protocol == null)
            {
                if (_logger.IsTrace) _logger.Warn($"Received a message from node: {RemoteNodeId}, ({dynamicMessageCode} => {messageId}), known protocols ({_protocols.Count}): {string.Join(", ", _protocols.Select(x => $"{x.Key}.{x.Value.ProtocolVersion} {x.Value.MessageIdSpaceSize}"))}");
                return;
            }

            packet.PacketType = messageId;
            _protocols[protocol].HandleMessage(packet);
        }

        public void Init(byte p2PVersion, IChannelHandlerContext context, IPacketSender packetSender)
        {
            if (_logger.IsTrace) _logger.Trace($"{nameof(Init)} called on {this}");

            if (context == null) throw new ArgumentNullException(nameof(context));
            if (packetSender == null) throw new ArgumentNullException(nameof(packetSender));

            P2PVersion = p2PVersion;
            lock (_sessionStateLock)
            {
                if (IsClosing)
                {
                    return;
                }

                if (State != SessionState.HandshakeComplete)
                {
                    throw new InvalidOperationException($"{nameof(Init)} called on {this}");
                }

                _context = context;
                _packetSender = packetSender;
                State = SessionState.Initialized;
            }

            Initialized?.Invoke(this, EventArgs.Empty);
        }

        public void Handshake(PublicKey handshakeRemoteNodeId)
        {
            if (_logger.IsTrace) _logger.Trace($"{nameof(Handshake)} called on {this}");
            lock (_sessionStateLock)
            {
                if (State == SessionState.Initialized || State == SessionState.HandshakeComplete)
                {
                    throw new InvalidOperationException($"{nameof(Handshake)} called on {this}");
                }

                if (IsClosing)
                {
                    return;
                }

                State = SessionState.HandshakeComplete;
            }

            //For IN connections we don't have NodeId until this moment, so we need to set it in Session
            //For OUT connections it is possible remote id is different than what we had persisted or received from Discovery
            //If that is the case we need to set it in the session
            if (RemoteNodeId == null)
            {
                RemoteNodeId = handshakeRemoteNodeId;
            }
            else if (handshakeRemoteNodeId != null && RemoteNodeId != handshakeRemoteNodeId)
            {
                if (_logger.IsTrace) _logger.Trace($"Different NodeId received in handshake: old: {RemoteNodeId}, new: {handshakeRemoteNodeId}");
                ObsoleteRemoteNodeId = RemoteNodeId;
                RemoteNodeId = handshakeRemoteNodeId;
                Node = new Node(RemoteNodeId, RemoteHost, RemotePort, _node.AddedToDiscovery);
            }

            Metrics.Handshakes++;

            HandshakeComplete?.Invoke(this, EventArgs.Empty);
        }

        public void InitiateDisconnect(DisconnectReason disconnectReason, string details = null)
        {
            lock (_sessionStateLock)
            {
                if (IsClosing)
                {
                    return;
                }

                State = SessionState.DisconnectingProtocols;
            }

            if (_logger.IsTrace) _logger.Trace($"|NetworkTrace| {this} disconnecting protocols");
            //Trigger disconnect on each protocol handler (if p2p is initialized it will send disconnect message to the peer)
            if (_protocols.Any())
            {
                foreach (var protocolHandler in _protocols.Values)
                {
                    try
                    {
                        if (_logger.IsTrace) _logger.Trace($"|NetworkTrace| {this} disconnecting {protocolHandler.ProtocolCode}.{protocolHandler.ProtocolVersion} {disconnectReason} ({details})");
                        protocolHandler.InitiateDisconnect(disconnectReason, details);
                    }
                    catch (Exception e)
                    {
                        if (_logger.IsDebug) _logger.Error($"DEBUG/ERROR Failed to disconnect {protocolHandler.ProtocolCode} {protocolHandler.ProtocolVersion} correctly", e);
                    }
                }
            }

            Disconnect(disconnectReason, DisconnectType.Local, details);
        }

        private object _sessionStateLock = new object();
        public byte P2PVersion { get; private set; }

        private SessionState _state;

        public SessionState State
        {
            get => _state;
            private set
            {
                _state = value;
                BestStateReached = (SessionState) Math.Min((int) SessionState.Initialized, (int) value);
            }
        }

        public SessionState BestStateReached { get; private set; }

        public void Disconnect(DisconnectReason disconnectReason, DisconnectType disconnectType, string details)
        {
            if (_logger.IsTrace) _logger.Trace($"|NetworkTrace| {this} disconnect call {disconnectReason} {disconnectType}");

            lock (_sessionStateLock)
            {
                if (State >= SessionState.Disconnecting)
                {
                    if (_logger.IsTrace) _logger.Trace($"|NetworkTrace| {this} already disconnected {disconnectReason} {disconnectType}");
                    return;
                }

                State = SessionState.Disconnecting;
            }

            UpdateMetric(disconnectType, disconnectReason);

            if (_logger.IsTrace) _logger.Trace($"|NetworkTrace| {this} invoking 'Disconnecting' event {disconnectReason} {disconnectType}");
            Disconnecting?.Invoke(this, new DisconnectEventArgs(disconnectReason, disconnectType, details));

            //Possible in case of disconnect before p2p initialization
            if (_context == null)
            {
                //in case pipeline did not get to p2p - no disconnect delay
                _channel.DisconnectAsync().ContinueWith(x =>
                {
                    if (x.IsFaulted && _logger.IsTrace) _logger.Trace($"Error while disconnecting on channel on {this} : {x.Exception}");
                });
            }
            else
            {
                var delayTask = disconnectType == DisconnectType.Local ? Task.Delay(Timeouts.Disconnection) : Task.CompletedTask;
                delayTask.ContinueWith(t =>
                {
                    if (_logger.IsTrace) _logger.Trace($"{this} disconnecting now after {Timeouts.Disconnection.TotalMilliseconds} milliseconds");
                    _context.DisconnectAsync().ContinueWith(x =>
                    {
                        if (x.IsFaulted && _logger.IsTrace) _logger.Trace($"Error while disconnecting on context on {this} : {x.Exception}");
                    });
                });
            }

            lock (_sessionStateLock)
            {
                State = SessionState.Disconnected;
            }

            if (Disconnected != null)
            {
                if (_logger.IsTrace) _logger.Trace($"|NetworkTrace| {this} disconnected event {disconnectReason} {disconnectType}");
                Disconnected?.Invoke(this, new DisconnectEventArgs(disconnectReason, disconnectType, details));
            }
            else if (_logger.IsDebug) _logger.Error($"DEBUG/ERROR  No subscriptions for session disconnected event on {this}");
        }

        private static void UpdateMetric(DisconnectType disconnectType, DisconnectReason disconnectReason)
        {
            if (disconnectType == DisconnectType.Remote)
            {
                switch (disconnectReason)
                {
                    case DisconnectReason.BreachOfProtocol:
                        Metrics.BreachOfProtocolDisconnects++;
                        break;
                    case DisconnectReason.UselessPeer:
                        Metrics.UselessPeerDisconnects++;
                        break;
                    case DisconnectReason.TooManyPeers:
                        Metrics.TooManyPeersDisconnects++;
                        break;
                    case DisconnectReason.AlreadyConnected:
                        Metrics.AlreadyConnectedDisconnects++;
                        break;
                    case DisconnectReason.IncompatibleP2PVersion:
                        Metrics.IncompatibleP2PDisconnects++;
                        break;
                    case DisconnectReason.NullNodeIdentityReceived:
                        Metrics.NullNodeIdentityDisconnects++;
                        break;
                    case DisconnectReason.ClientQuitting:
                        Metrics.ClientQuittingDisconnects++;
                        break;
                    case DisconnectReason.UnexpectedIdentity:
                        Metrics.UnexpectedIdentityDisconnects++;
                        break;
                    case DisconnectReason.ReceiveMessageTimeout:
                        Metrics.ReceiveMessageTimeoutDisconnects++;
                        break;
                    case DisconnectReason.DisconnectRequested:
                        Metrics.DisconnectRequestedDisconnects++;
                        break;
                    case DisconnectReason.IdentitySameAsSelf:
                        Metrics.SameAsSelfDisconnects++;
                        break;
                    case DisconnectReason.TcpSubSystemError:
                        Metrics.TcpSubsystemErrorDisconnects++;
                        break;
                    default:
                        Metrics.OtherDisconnects++;
                        break;
                }
            }

            if (disconnectType == DisconnectType.Local)
            {
                switch (disconnectReason)
                {
                    case DisconnectReason.BreachOfProtocol:
                        Metrics.LocalBreachOfProtocolDisconnects++;
                        break;
                    case DisconnectReason.UselessPeer:
                        Metrics.LocalUselessPeerDisconnects++;
                        break;
                    case DisconnectReason.TooManyPeers:
                        Metrics.LocalTooManyPeersDisconnects++;
                        break;
                    case DisconnectReason.AlreadyConnected:
                        Metrics.LocalAlreadyConnectedDisconnects++;
                        break;
                    case DisconnectReason.IncompatibleP2PVersion:
                        Metrics.LocalIncompatibleP2PDisconnects++;
                        break;
                    case DisconnectReason.NullNodeIdentityReceived:
                        Metrics.LocalNullNodeIdentityDisconnects++;
                        break;
                    case DisconnectReason.ClientQuitting:
                        Metrics.LocalClientQuittingDisconnects++;
                        break;
                    case DisconnectReason.UnexpectedIdentity:
                        Metrics.LocalUnexpectedIdentityDisconnects++;
                        break;
                    case DisconnectReason.ReceiveMessageTimeout:
                        Metrics.LocalReceiveMessageTimeoutDisconnects++;
                        break;
                    case DisconnectReason.DisconnectRequested:
                        Metrics.LocalDisconnectRequestedDisconnects++;
                        break;
                    case DisconnectReason.IdentitySameAsSelf:
                        Metrics.LocalSameAsSelfDisconnects++;
                        break;
                    case DisconnectReason.TcpSubSystemError:
                        Metrics.LocalTcpSubsystemErrorDisconnects++;
                        break;
                    default:
                        Metrics.LocalOtherDisconnects++;
                        break;
                }
            }
        }

        public event EventHandler<DisconnectEventArgs> Disconnecting;
        public event EventHandler<DisconnectEventArgs> Disconnected;
        public event EventHandler<EventArgs> HandshakeComplete;
        public event EventHandler<EventArgs> Initialized;

        public void Dispose()
        {
            lock (_sessionStateLock)
            {
                if (State != SessionState.Disconnected)
                {
                    throw new InvalidOperationException($"Disposing {this}");
                }
            }

            foreach ((_, IProtocolHandler handler) in _protocols)
            {
                handler.Dispose();
            }
        }

        private IPacketSender _packetSender;

        public void AddProtocolHandler(IProtocolHandler handler)
        {
            if (_protocols.ContainsKey(handler.ProtocolCode))
            {
                throw new InvalidOperationException($"{this} already has {handler.ProtocolCode} started");
            }

            if (handler.ProtocolCode != Protocol.P2P && !_protocols.ContainsKey(Protocol.P2P))
            {
                throw new InvalidOperationException($"{Protocol.P2P} protocol handler has to be started before starting {handler.ProtocolCode} protocol handler on {this}");
            }

            _protocols.Add(handler.ProtocolCode, handler);
            _resolver = GetOrCreateResolver();
        }

        private AdaptiveCodeResolver GetOrCreateResolver()
        {
            string key = string.Join(":", _protocols.Select(p => p.Key).OrderBy(x => x).ToArray());
            if (!_resolvers.ContainsKey(key))
            {
                _resolvers[key] = new AdaptiveCodeResolver(_protocols);
            }

            return _resolvers[key];
        }

        public override string ToString()
        {
            string formattedRemoteHost = RemoteHost?.Replace("::ffff:", string.Empty);
            return Direction == ConnectionDirection.In ? $"{State} {Direction} session {formattedRemoteHost}:{RemotePort}->localhost:{LocalPort}" : $"{State} {Direction} session localhost:{LocalPort}->{formattedRemoteHost}:{RemotePort}";
        }
        
        private AdaptiveCodeResolver _resolver;
        
        private class AdaptiveCodeResolver
        {
            private readonly (string ProtocolCode, int SpaceSize)[] _alphabetically;

            public AdaptiveCodeResolver(Dictionary<string, IProtocolHandler> protocols)
            {
                _alphabetically = new (string, int)[protocols.Count];
                _alphabetically[0] = (Protocol.P2P, protocols[Protocol.P2P].MessageIdSpaceSize);
                int i = 1;
                foreach (KeyValuePair<string, IProtocolHandler> protocolSession in protocols.Where(kv => kv.Key != "p2p").OrderBy(kv => kv.Key))
                {
                    _alphabetically[i++] = (protocolSession.Key, protocolSession.Value.MessageIdSpaceSize);
                }
            }

            public (string, int) ResolveProtocol(int adaptiveId)
            {
                int offset = 0;
                for (int j = 0; j < _alphabetically.Length; j++)
                {
                    if (offset + _alphabetically[j].SpaceSize > adaptiveId)
                    {
                        return (_alphabetically[j].ProtocolCode, adaptiveId - offset);
                    }

                    offset += _alphabetically[j].SpaceSize;
                }

                // consider disconnecting on the breach of protocol here?
                return (null, 0);
            }

            public int ResolveAdaptiveId(string protocol, int messageCode)
            {
                int offset = 0;
                for (int j = 0; j < _alphabetically.Length; j++)
                {
                    if (_alphabetically[j].ProtocolCode == protocol)
                    {
                        if (_alphabetically[j].SpaceSize <= messageCode)
                        {
                            break;
                        }

                        return offset + messageCode;
                    }

                    offset += _alphabetically[j].SpaceSize;
                }

                throw new InvalidOperationException($"Registered protocols do not support {protocol}.{messageCode}");
            }
        }
    }
}