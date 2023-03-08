// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DotNetty.Transport.Channels;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.Network.Contract.P2P;
using Nethermind.Network.P2P.Analyzers;
using Nethermind.Network.P2P.EventArg;
using Nethermind.Network.P2P.Messages;
using Nethermind.Network.P2P.ProtocolHandlers;
using Nethermind.Network.Rlpx;
using Nethermind.Stats.Model;

namespace Nethermind.Network.P2P
{
    public class Session : ISession
    {
        private static readonly ConcurrentDictionary<string, AdaptiveCodeResolver> _resolvers = new();
        private readonly ConcurrentDictionary<string, IProtocolHandler> _protocols = new();

        private readonly ILogger _logger;
        private readonly ILogManager _logManager;

        private Node? _node;
        private readonly IChannel _channel;
        private readonly IDisconnectsAnalyzer _disconnectsAnalyzer;
        private IChannelHandlerContext? _context;

        public Session(
            int localPort,
            IChannel channel,
            IDisconnectsAnalyzer disconnectsAnalyzer,
            ILogManager logManager)
        {
            Direction = ConnectionDirection.In;
            State = SessionState.New;
            _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
            _channel = channel ?? throw new ArgumentNullException(nameof(channel));
            _disconnectsAnalyzer = disconnectsAnalyzer;
            _logger = logManager.GetClassLogger<Session>();
            RemoteNodeId = null;
            LocalPort = localPort;
            SessionId = Guid.NewGuid();
        }

        public Session(
            int localPort,
            Node remoteNode,
            IChannel channel,
            IDisconnectsAnalyzer disconnectsAnalyzer,
            ILogManager logManager)
        {
            State = SessionState.New;
            _node = remoteNode ?? throw new ArgumentNullException(nameof(remoteNode));
            _channel = channel ?? throw new ArgumentNullException(nameof(channel));
            _disconnectsAnalyzer = disconnectsAnalyzer;
            _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
            _logger = logManager.GetClassLogger<Session>();
            RemoteNodeId = remoteNode.Id;
            RemoteHost = remoteNode.Host;
            RemotePort = remoteNode.Port;
            LocalPort = localPort;
            SessionId = Guid.NewGuid();
            Direction = ConnectionDirection.Out;
        }

        public bool IsClosing => State > SessionState.Initialized;
        public bool IsNetworkIdMatched { get; set; }
        public int LocalPort { get; set; }
        public PublicKey? RemoteNodeId { get; set; }
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
                if (_node is null)
                {
                    if (RemoteNodeId is null || RemoteHost is null || RemotePort == 0)
                    {
                        throw new InvalidOperationException("Cannot create a session's node object without knowing remote node details");
                    }

                    _node = new Node(RemoteNodeId, RemoteHost, RemotePort);
                }

                return _node;
            }

            private set => _node = value;
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
            if (!_protocols.TryGetValue(Protocol.P2P, out IProtocolHandler protocol))
            {
                return;
            }
            if (protocol is IP2PProtocolHandler p2PProtocol)
            {
                p2PProtocol.AddSupportedCapability(capability);
            }
        }

        public bool HasAvailableCapability(Capability capability)
            => _protocols.TryGetValue(Protocol.P2P, out IProtocolHandler protocol)
               && protocol is IP2PProtocolHandler p2PProtocol
               && p2PProtocol.HasAvailableCapability(capability);

        public bool HasAgreedCapability(Capability capability)
            => _protocols.TryGetValue(Protocol.P2P, out IProtocolHandler protocol)
               && protocol is IP2PProtocolHandler p2PProtocol
               && p2PProtocol.HasAgreedCapability(capability);

        public IPingSender PingSender { get; set; }

        public void ReceiveMessage(ZeroPacket zeroPacket)
        {
            Interlocked.Add(ref Metrics.P2PBytesReceived, zeroPacket.Content.ReadableBytes);

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
            (string? protocol, int messageId) = _resolver.ResolveProtocol(zeroPacket.PacketType);
            zeroPacket.Protocol = protocol;

            if (_logger.IsTrace)
                _logger.Trace($"{this} received a message of length {zeroPacket.Content.ReadableBytes} " +
                              $"({dynamicMessageCode} => {protocol}.{messageId})");

            if (protocol is null)
            {
                if (_logger.IsTrace)
                    _logger.Warn($"Received a message from node: {RemoteNodeId}, " +
                                 $"({dynamicMessageCode} => {messageId}), known protocols ({_protocols.Count}): " +
                                 $"{string.Join(", ", _protocols.Select(x => $"{x.Value.Name} {x.Value.MessageIdSpaceSize}"))}");
                return;
            }

            zeroPacket.PacketType = (byte)messageId;
            IProtocolHandler protocolHandler = _protocols[protocol];
            if (protocolHandler is IZeroProtocolHandler zeroProtocolHandler)
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
            var size = _packetSender.Enqueue(message);
            Interlocked.Add(ref Metrics.P2PBytesSent, size);
        }

        public void ReceiveMessage(Packet packet)
        {
            Interlocked.Add(ref Metrics.P2PBytesReceived, packet.Data.Length);

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

            if (_logger.IsTrace)
                _logger.Trace($"{this} received a message of length {packet.Data.Length} " +
                              $"({dynamicMessageCode} => {protocol}.{messageId})");

            if (protocol is null)
            {
                if (_logger.IsTrace)
                    _logger.Warn($"Received a message from node: {RemoteNodeId}, ({dynamicMessageCode} => {messageId}), " +
                                 $"known protocols ({_protocols.Count}): " +
                                 $"{string.Join(", ", _protocols.Select(x => $"{x.Value.Name} {x.Value.MessageIdSpaceSize}"))}");
                return;
            }

            packet.PacketType = messageId;

            if (State < SessionState.DisconnectingProtocols)
            {
                _protocols[protocol].HandleMessage(packet);
            }
        }

        public bool TryGetProtocolHandler(string protocolCode, out IProtocolHandler handler)
        {
            return _protocols.TryGetValue(protocolCode, out handler);
        }

        public void Init(byte p2PVersion, IChannelHandlerContext context, IPacketSender packetSender)
        {
            if (_logger.IsTrace) _logger.Trace($"{nameof(Init)} called on {this}");

            if (context is null) throw new ArgumentNullException(nameof(context));
            if (packetSender is null) throw new ArgumentNullException(nameof(packetSender));

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

        public void Handshake(PublicKey? handshakeRemoteNodeId)
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
            if (RemoteNodeId is null)
            {
                RemoteNodeId = handshakeRemoteNodeId;
            }
            else if (handshakeRemoteNodeId is not null && RemoteNodeId != handshakeRemoteNodeId)
            {
                if (_logger.IsTrace)
                    _logger.Trace($"Different NodeId received in handshake: old: {RemoteNodeId}, new: {handshakeRemoteNodeId}");
                ObsoleteRemoteNodeId = RemoteNodeId;
                RemoteNodeId = handshakeRemoteNodeId;
                Node = new Node(RemoteNodeId, RemoteHost, RemotePort);
            }

            Metrics.Handshakes++;

            HandshakeComplete?.Invoke(this, EventArgs.Empty);
        }

        public void InitiateDisconnect(InitiateDisconnectReason initiateDisconnectReason, string? details = null)
        {
            DisconnectReason disconnectReason = initiateDisconnectReason.ToDisconnectReason();

            bool ShouldDisconnectStaticNode()
            {
                switch (disconnectReason)
                {
                    case DisconnectReason.DisconnectRequested:
                    case DisconnectReason.TcpSubSystemError:
                    case DisconnectReason.UselessPeer:
                    case DisconnectReason.TooManyPeers:
                    case DisconnectReason.Other:
                        return false;
                    case DisconnectReason.ReceiveMessageTimeout:
                    case DisconnectReason.BreachOfProtocol:
                    case DisconnectReason.AlreadyConnected:
                    case DisconnectReason.IncompatibleP2PVersion:
                    case DisconnectReason.NullNodeIdentityReceived:
                    case DisconnectReason.ClientQuitting:
                    case DisconnectReason.UnexpectedIdentity:
                    case DisconnectReason.IdentitySameAsSelf:
                        return true;
                    default:
                        return true;
                }
            }

            if (Node?.IsStatic == true && !ShouldDisconnectStaticNode())
            {
                if (_logger.IsTrace) _logger.Trace($"{this} not disconnecting for static peer on {disconnectReason} ({details})");
                return;
            }

            lock (_sessionStateLock)
            {
                if (IsClosing)
                {
                    return;
                }

                State = SessionState.DisconnectingProtocols;
            }

            if (_logger.IsDebug) _logger.Debug($"{this} initiating disconnect because {initiateDisconnectReason}, details: {details}");
            //Trigger disconnect on each protocol handler (if p2p is initialized it will send disconnect message to the peer)
            if (_protocols.Any())
            {
                foreach (IProtocolHandler protocolHandler in _protocols.Values)
                {
                    try
                    {
                        if (_logger.IsTrace)
                            _logger.Trace($"{this} disconnecting {protocolHandler.Name} {initiateDisconnectReason} ({details})");
                        protocolHandler.DisconnectProtocol(disconnectReason, details);
                    }
                    catch (Exception e)
                    {
                        if (_logger.IsDebug)
                            _logger.Error($"DEBUG/ERROR Failed to disconnect {protocolHandler.Name} correctly", e);
                    }
                }
            }

            MarkDisconnected(disconnectReason, DisconnectType.Local, details);
        }

        private object _sessionStateLock = new();
        public byte P2PVersion { get; private set; }

        private SessionState _state;

        public SessionState State
        {
            get => _state;
            private set
            {
                _state = value;
                BestStateReached = (SessionState)Math.Min((int)SessionState.Initialized, (int)value);
            }
        }

        public SessionState BestStateReached { get; private set; }

        public void MarkDisconnected(DisconnectReason disconnectReason, DisconnectType disconnectType, string details)
        {
            lock (_sessionStateLock)
            {
                if (State >= SessionState.Disconnecting)
                {
                    if (_logger.IsTrace)
                        _logger.Trace($"{this} already disconnected {disconnectReason} {disconnectType}");
                    return;
                }

                State = SessionState.Disconnecting;
            }

            if (_isTracked)
            {
                _logger.Warn($"Tracked {this} -> disconnected {disconnectType} {disconnectReason} {details}");
            }

            _disconnectsAnalyzer.ReportDisconnect(disconnectReason, disconnectType, details);

            if (NetworkDiagTracer.IsEnabled && RemoteHost is not null)
                NetworkDiagTracer.ReportDisconnect(Node.Address, $"{disconnectType} {disconnectReason} {details}");

            if (BestStateReached >= SessionState.Initialized && disconnectReason != DisconnectReason.TooManyPeers)
            {
                // TooManyPeers is a benign disconnect that we should not be worried about - many peers are running at their limit
                // also any disconnects before the handshake and init do not have to be logged as they are most likely just rejecting any connections
                if (_logger.IsTrace && HasAgreedCapability(new Capability(Protocol.Eth, 66)) && IsNetworkIdMatched)
                {
                    if (_logger.IsError)
                        _logger.Error(
                            $"{this} invoking 'Disconnecting' event {disconnectReason} {disconnectType} {details}");
                }
            }
            else
            {
                if (_logger.IsTrace)
                    _logger.Trace($"{this} invoking 'Disconnecting' event {disconnectReason} {disconnectType} {details}");
            }

            Disconnecting?.Invoke(this, new DisconnectEventArgs(disconnectReason, disconnectType, details));

            //Possible in case of disconnect before p2p initialization
            if (_context is null)
            {
                //in case pipeline did not get to p2p - no disconnect delay
                _channel.DisconnectAsync().ContinueWith(x =>
                {
                    if (x.IsFaulted && _logger.IsTrace)
                        _logger.Trace($"Error while disconnecting on channel on {this} : {x.Exception}");
                });
            }
            else
            {
                Task delayTask =
                    disconnectType == DisconnectType.Local
                        ? Task.Delay(Timeouts.Disconnection)
                        : Task.CompletedTask;
                delayTask.ContinueWith(t =>
                {
                    if (_logger.IsTrace)
                        _logger.Trace($"{this} disconnecting now after {Timeouts.Disconnection.TotalMilliseconds} milliseconds");
                    _context.DisconnectAsync().ContinueWith(x =>
                    {
                        if (x.IsFaulted && _logger.IsTrace)
                            _logger.Trace($"Error while disconnecting on context on {this} : {x.Exception}");
                    });
                });
            }

            lock (_sessionStateLock)
            {
                State = SessionState.Disconnected;
            }

            if (Disconnected is not null)
            {
                if (_logger.IsTrace)
                    _logger.Trace($"|NetworkTrace| {this} disconnected event {disconnectReason} {disconnectType}");
                Disconnected?.Invoke(this, new DisconnectEventArgs(disconnectReason, disconnectType, details));
            }
            else if (_logger.IsDebug)
                _logger.Error($"DEBUG/ERROR  No subscriptions for session disconnected event on {this}");
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
                throw new InvalidOperationException(
                    $"{Protocol.P2P} handler has to be started before starting {handler.ProtocolCode} handler on {this}");
            }

            _protocols.TryAdd(handler.ProtocolCode, handler);
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
            return Direction == ConnectionDirection.In
                ? $"[Session|{Direction}|{State}|{formattedRemoteHost}:{RemotePort}->{LocalPort}]"
                : $"[Session|{Direction}|{State}|{LocalPort}->{formattedRemoteHost}:{RemotePort}]";
        }

        private AdaptiveCodeResolver _resolver;

        private class AdaptiveCodeResolver
        {
            private readonly (string ProtocolCode, int SpaceSize)[] _alphabetically;

            public AdaptiveCodeResolver(IDictionary<string, IProtocolHandler> protocols)
            {
                _alphabetically = new (string, int)[protocols.Count];
                _alphabetically[0] = (Protocol.P2P, protocols[Protocol.P2P].MessageIdSpaceSize);
                int i = 1;
                foreach (KeyValuePair<string, IProtocolHandler> protocolSession
                    in protocols.Where(kv => kv.Key != Protocol.P2P).OrderBy(kv => kv.Key))
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

        private bool _isTracked = false;

        public void StartTrackingSession()
        {
            _isTracked = true;
        }
    }
}
