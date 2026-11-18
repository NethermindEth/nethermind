// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Net;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using DotNetty.Transport.Channels;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Metric;
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
    internal interface ISessionActivityObserver
    {
        void OnSessionActivity(Session session);

        void OnSessionDisconnected(Session session, DisconnectEventArgs args);
    }

    public class Session : ISession
    {
        private static readonly ConcurrentDictionary<string, AdaptiveCodeResolver> _resolvers = new();
        private readonly ConcurrentDictionary<string, IProtocolHandler> _protocols = new(concurrencyLevel: 1, capacity: 4);

        private readonly ILogger _logger;
        private readonly ILogManager _logManager;
        private readonly DisconnectEventHandlers _disconnectedHandlers = new();

        private Node? _node;
        private readonly IChannel _channel;
        private readonly IDisconnectsAnalyzer _disconnectsAnalyzer;
        private IChannelHandlerContext? _context;
        private volatile ISessionActivityObserver? _activityObserver;
        private volatile bool _isChannelClosed;
        private volatile bool _isAuthenticated;
        private volatile bool _hasUsefulSyncResponse;

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
        private bool IsClosed => State > SessionState.DisconnectingProtocols;
        public bool IsChannelClosed => _isChannelClosed;
        internal bool IsChannelWritable => _channel.IsWritable;
        internal bool HasUsefulSyncResponse => _hasUsefulSyncResponse;
        internal PacketReceiptTracker InboundReceipts { get; } = new();
        bool ISession.IsChannelWritable => IsChannelWritable;
        PacketReceiptTracker ISession.InboundReceipts => InboundReceipts;
        public bool IsOutboundBufferFull => _context?.Channel is { IsWritable: false };
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
                        ThrowMissingNodeDetails();
                    }

                    _node = new Node(RemoteNodeId, RemoteHost, RemotePort);
                }

                [DoesNotReturn, StackTraceHidden]
                static void ThrowMissingNodeDetails() => throw new InvalidOperationException("Cannot create a session's node object without knowing remote node details");

                return _node;
            }

            private set => _node = value;
        }

        private IPEndPoint? GetRemoteEndPoint() =>
            _node?.Address ?? (IPAddress.TryParse(RemoteHost, out IPAddress? address) ? new IPEndPoint(address, RemotePort) : null);

        public void EnableSnappy()
        {
            lock (_sessionStateLock)
            {
                if (State < SessionState.Initialized)
                {
                    ThrowInvalidSessionState();
                }

                if (IsClosing)
                {
                    return;
                }
            }

            if (_logger.IsTrace) TraceEnablingSnappy();

            // since groups were used, we are on a different thread
            _context.Channel.Pipeline.Get<ZeroNettyP2PHandler>()?.EnableSnappy();
            ZeroPacketSplitter splitter = _context.Channel.Pipeline.Get<ZeroPacketSplitter>()
                ?? throw new InvalidOperationException("Cannot enable Snappy without the outbound ZeroPacketSplitter.");
            splitter.EnableSnappy(_logManager);

            [MethodImpl(MethodImplOptions.NoInlining)]
            void TraceEnablingSnappy() => _logger.Trace($"Enabling Snappy compression and disabling framing in {this:hide}");
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

        private (DisconnectReason, string?)? _disconnectAfterInitialized = null;

        private long _bytesReceived;
        public long BytesReceived => Interlocked.Read(ref _bytesReceived);

        public void ReceiveMessage(ZeroPacket zeroPacket)
        {
            Interlocked.Add(ref Metrics.P2PBytesReceived, zeroPacket.Content.Length);
            Interlocked.Add(ref _bytesReceived, zeroPacket.Content.Length);

            lock (_sessionStateLock)
            {
                if (State < SessionState.Initialized)
                {
                    ThrowInvalidSessionState();
                }

                if (IsClosing)
                {
                    return;
                }
            }

            int dynamicMessageCode = zeroPacket.PacketType;
            MessageRoute? route = GetIncomingRoute(_routes, dynamicMessageCode);
            string? protocol = route?.Protocol;
            int messageId = route?.MessageId ?? 0;
            if (route is null)
            {
                InitiateDisconnect(DisconnectReason.BreachOfProtocol, $"message id {dynamicMessageCode} outside negotiated protocols");
                return;
            }
            zeroPacket.Protocol = protocol;

            _activityObserver?.OnSessionActivity(this);
            MsgReceived?.Invoke(this, new PeerEventArgs(_node, zeroPacket.Protocol, zeroPacket.PacketType, zeroPacket.Content.Length));

            route?.Record(zeroPacket.Content.Length);

            if (_logger.IsTrace) TraceMessageReceived(dynamicMessageCode, protocol, messageId, zeroPacket.Content.Length);

            zeroPacket.PacketType = (byte)messageId;
            IProtocolHandler protocolHandler = route!.Handler;
            if (protocolHandler is IZeroProtocolHandler zeroProtocolHandler)
            {
                zeroProtocolHandler.HandleMessage(zeroPacket);
            }
            else
            {
                protocolHandler.HandleMessage(new Packet(zeroPacket));
            }

            [MethodImpl(MethodImplOptions.NoInlining)]
            void TraceMessageReceived(int dynamicCode, string? proto, int msgId, int readableBytes)
                => _logger.Trace($"{this:hide} received a message of length {readableBytes} ({dynamicCode} => {proto}.{msgId})");
        }

        public int DeliverMessage<T>(T message) where T : P2PMessage
        {
            try
            {
                lock (_sessionStateLock)
                {
                    if (State < SessionState.Initialized)
                    {
                        ThrowInvalidSessionState();
                    }

                    // Must allow sending out packet when `DisconnectingProtocols` so that we can send out disconnect reason
                    // and hello (part of protocol)
                    if (IsClosed)
                    {
                        return 1;
                    }
                }

                if (_logger.IsTrace) TraceDeliverMessage(message);

                MessageRoutes routes = _routes;
                int adaptivePacketType = routes.Resolver.ResolveAdaptiveId(message.Protocol, message.PacketType);
                message.AdaptivePacketType = adaptivePacketType;
                int size = _packetSender.Enqueue(message);

                _activityObserver?.OnSessionActivity(this);
                MsgDelivered?.Invoke(this, new PeerEventArgs(_node, message.Protocol, message.PacketType, size));

                GetOutgoingRoute(routes, adaptivePacketType, message.Protocol, message.PacketType).Record(size);

                Interlocked.Add(ref Metrics.P2PBytesSent, size);

                return size;
            }
            finally
            {
                message.Dispose();
            }

            [MethodImpl(MethodImplOptions.NoInlining)]
            void TraceDeliverMessage(T msg) => _logger.Trace($"P2P to deliver {msg.Protocol}.{msg.PacketType} on {this:hide}");
        }

        public bool TryGetProtocolHandler(string protocolCode, out IProtocolHandler handler) => _protocols.TryGetValue(protocolCode, out handler);

        public void Init(byte p2PVersion, IChannelHandlerContext context, IPacketSender packetSender)
        {
            if (_logger.IsTrace) TraceSessionOperation();

            ArgumentNullException.ThrowIfNull(context);
            ArgumentNullException.ThrowIfNull(packetSender);

            P2PVersion = p2PVersion;
            lock (_sessionStateLock)
            {
                if (IsClosing)
                {
                    return;
                }

                if (State != SessionState.HandshakeComplete)
                {
                    ThrowInvalidSessionState();
                }

                _context = context;
                _packetSender = packetSender;
                State = SessionState.Initialized;
            }

            Initialized?.Invoke(this, EventArgs.Empty);

            // Disconnect may send disconnect reason message. But the hello message must be sent first, which is done
            // during Initialized event.
            // https://github.com/ethereum/devp2p/blob/master/rlpx.md#user-content-hello-0x00
            if (_disconnectAfterInitialized is not null)
            {
                InitiateDisconnect(_disconnectAfterInitialized.Value.Item1, _disconnectAfterInitialized.Value.Item2);
                _disconnectAfterInitialized = null;
            }

            [MethodImpl(MethodImplOptions.NoInlining)]
            void TraceSessionOperation() => _logger.Trace($"{nameof(Init)} called on {this:hide}");
        }

        public void Handshake(PublicKey? handshakeRemoteNodeId)
        {
            if (_logger.IsTrace) TraceHandshakeCalled();
            lock (_sessionStateLock)
            {
                if (State == SessionState.Initialized || State == SessionState.HandshakeComplete)
                {
                    ThrowInvalidSessionState();
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
                if (Direction == ConnectionDirection.Out)
                {
                    if (_logger.IsDebug) DebugUnexpectedNodeId(handshakeRemoteNodeId);
                    InitiateDisconnect(DisconnectReason.UnexpectedIdentity, $"expected {RemoteNodeId}, received {handshakeRemoteNodeId}");
                }
                else
                {
                    if (_logger.IsTrace) TraceDifferentNodeId(handshakeRemoteNodeId);
                    ObsoleteRemoteNodeId = RemoteNodeId;
                    RemoteNodeId = handshakeRemoteNodeId;
                    Node = new Node(RemoteNodeId, RemoteHost, RemotePort);
                }
            }

            Metrics.Handshakes++;

            HandshakeComplete?.Invoke(this, EventArgs.Empty);

            [MethodImpl(MethodImplOptions.NoInlining)]
            void TraceHandshakeCalled() => _logger.Trace($"{nameof(Handshake)} called on {this:hide}");

            [MethodImpl(MethodImplOptions.NoInlining)]
            void DebugUnexpectedNodeId(PublicKey remoteNodeId) => _logger.Debug($"Unexpected remote node id in handshake: expected {RemoteNodeId:hide}, received {remoteNodeId:hide}");

            [MethodImpl(MethodImplOptions.NoInlining)]
            void TraceDifferentNodeId(PublicKey remoteNodeId) => _logger.Trace($"Different NodeId received in handshake: old: {RemoteNodeId:hide}, new: {remoteNodeId:hide}");
        }

        public void InitiateDisconnect(DisconnectReason disconnectReason, string? details = null)
        {
            EthDisconnectReason ethDisconnectReason = disconnectReason.ToEthDisconnectReason();

            bool ShouldDisconnectPrivilegedNode() => ethDisconnectReason switch
            {
                EthDisconnectReason.DisconnectRequested or EthDisconnectReason.TcpSubSystemError or EthDisconnectReason.UselessPeer or EthDisconnectReason.TooManyPeers or EthDisconnectReason.Other => false,
                EthDisconnectReason.ReceiveMessageTimeout or EthDisconnectReason.BreachOfProtocol or EthDisconnectReason.AlreadyConnected or EthDisconnectReason.IncompatibleP2PVersion or EthDisconnectReason.NullNodeIdentityReceived or EthDisconnectReason.ClientQuitting or EthDisconnectReason.UnexpectedIdentity or EthDisconnectReason.IdentitySameAsSelf => true,
                _ => true,
            };

            if ((_node?.IsStatic == true || _node?.IsTrusted == true) && !ShouldDisconnectPrivilegedNode())
            {
                if (_logger.IsTrace) TracePrivilegedPeerNotDisconnecting(disconnectReason, details);
                return;
            }

            lock (_sessionStateLock)
            {
                if (IsClosing)
                {
                    return;
                }

                if (State <= SessionState.HandshakeComplete)
                {
                    if (_disconnectAfterInitialized is not null) return;

                    _disconnectAfterInitialized = (disconnectReason, details);
                    return;
                }

                State = SessionState.DisconnectingProtocols;
            }

            InboundReceipts.Close();

            if (_logger.IsTrace) TraceInitiatingDisconnect(disconnectReason, details);

            //Trigger disconnect on each protocol handler (if p2p is initialized it will send disconnect message to the peer)
            if (!_protocols.IsEmpty)
            {
                foreach (KeyValuePair<string, IProtocolHandler> kvp in _protocols)
                {
                    IProtocolHandler protocolHandler = kvp.Value;
                    try
                    {
                        if (_logger.IsTrace) TraceDisconnectingProtocol(protocolHandler, disconnectReason, details);
                        protocolHandler.DisconnectProtocol(disconnectReason, details);
                    }
                    catch (Exception e)
                    {
                        if (_logger.IsDebug) DebugDisconnectProtocolFailed(protocolHandler, e);
                    }
                }
            }

            MarkDisconnected(disconnectReason, DisconnectType.Local, details);

            [MethodImpl(MethodImplOptions.NoInlining)]
            void TracePrivilegedPeerNotDisconnecting(DisconnectReason reason, string? det)
                => _logger.Trace($"{this:hide} not disconnecting for static/trusted peer on {reason} ({det:hide})");

            [MethodImpl(MethodImplOptions.NoInlining)]
            void TraceInitiatingDisconnect(DisconnectReason reason, string? det) =>
                _logger.Trace($"{this:hide} initiating disconnect because {reason}, details: {det:hide}");

            [MethodImpl(MethodImplOptions.NoInlining)]
            void TraceDisconnectingProtocol(IProtocolHandler handler, DisconnectReason reason, string? det)
                => _logger.Trace($"{this:hide} disconnecting {handler.Name} {reason} ({det:hide})");

            [MethodImpl(MethodImplOptions.NoInlining)]
            void DebugDisconnectProtocolFailed(IProtocolHandler handler, Exception e)
                => _logger.DebugError($"Failed to disconnect {handler.Name} correctly", e);
        }

        private readonly Lock _sessionStateLock = new();
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
                    if (_logger.IsTrace) TraceAlreadyDisconnected(disconnectReason, disconnectType);
                    return;
                }

                State = SessionState.Disconnecting;
            }

            InboundReceipts.Close();

            if (_isTracked) WarnTrackedDisconnect(disconnectReason, disconnectType, details);

            _disconnectsAnalyzer.ReportDisconnect(disconnectReason, disconnectType, details);

            if (NetworkDiagTracer.IsEnabled && RemoteHost is not null)
                NetworkDiagTracer.ReportDisconnect(GetRemoteEndPoint(), $"{disconnectType} {disconnectReason} {details}");

            if (BestStateReached >= SessionState.Initialized && disconnectReason != DisconnectReason.TooManyPeers)
            {
                // TooManyPeers is a benign disconnect that we should not be worried about - many peers are running at their limit
                // also any disconnects before the handshake and init do not have to be logged as they are most likely just rejecting any connections
                if (_logger.IsTrace && HasAgreedCapability(new Capability(Protocol.Eth, 66)) && IsNetworkIdMatched)
                {
                    if (_logger.IsError) ErrorDisconnectingEvent(disconnectReason, disconnectType, details);
                }
            }
            else
            {
                if (_logger.IsTrace) TraceDisconnectingEvent(disconnectReason, disconnectType, details);
            }

            DisconnectEventArgs disconnectEventArgs = new(disconnectReason, disconnectType, details);
            Disconnecting?.Invoke(this, disconnectEventArgs);

            _ = DisconnectAsync(disconnectType);

            lock (_sessionStateLock)
            {
                State = SessionState.Disconnected;
            }

            ISessionActivityObserver? activityObserver = _activityObserver;
            if (_disconnectedHandlers.HasHandlers)
            {
                if (_logger.IsTrace) TraceDisconnectedEvent(disconnectReason, disconnectType);
                _disconnectedHandlers.Invoke(this, disconnectEventArgs);
            }
            else if (_logger.IsDebug && activityObserver is null)
            {
                DebugNoDisconnectedSubscriptions();
            }

            activityObserver?.OnSessionDisconnected(this, disconnectEventArgs);

            [MethodImpl(MethodImplOptions.NoInlining)]
            void TraceAlreadyDisconnected(DisconnectReason reason, DisconnectType type)
                => _logger.Trace($"{this:hide} already disconnected {reason} {type}");

            [MethodImpl(MethodImplOptions.NoInlining)]
            void WarnTrackedDisconnect(DisconnectReason reason, DisconnectType type, string det)
                => _logger.Warn($"Tracked {this:hide} -> disconnected {type} {reason} {det:hide}");

            [MethodImpl(MethodImplOptions.NoInlining)]
            void ErrorDisconnectingEvent(DisconnectReason reason, DisconnectType type, string det)
                => _logger.Error($"{this:hide} invoking 'Disconnecting' event {reason} {type} {det:hide}");

            [MethodImpl(MethodImplOptions.NoInlining)]
            void TraceDisconnectingEvent(DisconnectReason reason, DisconnectType type, string det)
                => _logger.Trace($"{this:hide} invoking 'Disconnecting' event {reason} {type} {det:hide}");

            [MethodImpl(MethodImplOptions.NoInlining)]
            void TraceDisconnectedEvent(DisconnectReason reason, DisconnectType type)
                => _logger.Trace($"|NetworkTrace| {this:hide} disconnected event {reason} {type}");

            [MethodImpl(MethodImplOptions.NoInlining)]
            void DebugNoDisconnectedSubscriptions()
                => _logger.DebugError($"No subscriptions for session disconnected event on {this:hide}");
        }

        internal void MarkChannelClosed()
        {
            _isChannelClosed = true;
            InboundReceipts.Close();
        }

        private async Task DisconnectAsync(DisconnectType disconnectType)
        {
            //Possible in case of disconnect before p2p initialization
            if (_context is null)
            {
                //in case pipeline did not get to p2p - no disconnect delay
                try
                {
                    await _channel.DisconnectAsync();
                }
                catch (Exception e)
                {
                    if (_logger.IsTrace) TraceDisconnectError(e);
                }
            }
            else
            {
                if (disconnectType == DisconnectType.Local)
                {
                    await Task.Delay(Timeouts.Disconnection);
                }

                try
                {
                    await _context.DisconnectAsync();
                }
                catch (Exception e)
                {
                    if (_logger.IsTrace) TraceDisconnectError(e);
                }
            }

            [MethodImpl(MethodImplOptions.NoInlining)]
            void TraceDisconnectError(Exception e) => _logger.Trace($"Error while disconnecting on context on {this:hide} : {e:hide}");
        }

        public event EventHandler<DisconnectEventArgs> Disconnecting;
        public event EventHandler<DisconnectEventArgs> Disconnected
        {
            add => _disconnectedHandlers.Add(value);
            remove => _disconnectedHandlers.Remove(value);
        }
        public event EventHandler<EventArgs> HandshakeComplete;
        public event EventHandler<EventArgs> Initialized;
        internal event EventHandler<EventArgs>? Authenticated;
        public event EventHandler<PeerEventArgs> MsgReceived;
        public event EventHandler<PeerEventArgs> MsgDelivered;

        internal void SetActivityObserver(ISessionActivityObserver? activityObserver) => _activityObserver = activityObserver;

        internal void MarkAuthenticated()
        {
            _isAuthenticated = true;
            Authenticated?.Invoke(this, EventArgs.Empty);
        }

        void ISession.MarkAuthenticated() => MarkAuthenticated();

        internal void MarkUsefulSyncResponse()
        {
            if (_isAuthenticated && !IsClosing) _hasUsefulSyncResponse = true;
        }

        public void Dispose()
        {
            lock (_sessionStateLock)
            {
                if (State != SessionState.Disconnected)
                {
                    ThrowDisposingInvalidState();
                }
            }

            foreach ((_, IProtocolHandler handler) in _protocols)
            {
                handler.Dispose();
            }

            [DoesNotReturn, StackTraceHidden]
            void ThrowDisposingInvalidState() => throw new InvalidOperationException($"Disposing {this}");
        }

        private IPacketSender _packetSender;

        public void AddProtocolHandler(IProtocolHandler handler)
        {
            if (handler.ProtocolCode != Protocol.P2P && !_protocols.ContainsKey(Protocol.P2P))
            {
                ThrowP2PNotStarted(handler);
            }

            if (!_protocols.TryAdd(handler.ProtocolCode, handler))
            {
                ThrowProtocolAlreadyStarted(handler);
            }

            _routes = new MessageRoutes(GetOrCreateResolver(), _protocols);

            [DoesNotReturn, StackTraceHidden]
            void ThrowP2PNotStarted(IProtocolHandler h)
                => throw new InvalidOperationException(
                    $"{Protocol.P2P} handler has to be started before starting {h.ProtocolCode} handler on {this}");

            [DoesNotReturn, StackTraceHidden]
            void ThrowProtocolAlreadyStarted(IProtocolHandler h)
                => throw new InvalidOperationException($"{this} already has {h.ProtocolCode} started");
        }

        private AdaptiveCodeResolver GetOrCreateResolver()
        {
            string key = BuildResolverKey(_protocols);
            return _resolvers.GetOrAdd(
                key,
                static (_, protocols) => new AdaptiveCodeResolver(protocols),
                _protocols);

            static string BuildResolverKey(ConcurrentDictionary<string, IProtocolHandler> protocols)
            {
                // Typically 2-3 protocols; avoid LINQ OrderBy allocation
                string[] names = new string[protocols.Count];
                int i = 0;
                foreach (KeyValuePair<string, IProtocolHandler> p in protocols)
                {
                    if (i == names.Length)
                    {
                        Array.Resize(ref names, names.Length == 0 ? 4 : names.Length * 2);
                    }

                    names[i++] = p.Value.Name;
                }

                Array.Sort(names, 0, i, StringComparer.Ordinal);
                return string.Join(":", names.AsSpan(0, i));
            }
        }

        public override string ToString()
        {
            string formattedRemoteHost = RemoteHost?.Replace("::ffff:", string.Empty);
            return Direction == ConnectionDirection.In
                ? $"[Session|{Direction}|{State}|{formattedRemoteHost}:{RemotePort}->{LocalPort}]"
                : $"[Session|{Direction}|{State}|{LocalPort}->{formattedRemoteHost}:{RemotePort}]";
        }

        private MessageRoutes _routes;

        private class AdaptiveCodeResolver
        {
            private const int MaxAdaptiveIdCount = byte.MaxValue + 1;

            private readonly (string ProtocolCode, int SpaceSize)[] _alphabetically;

            public AdaptiveCodeResolver(IDictionary<string, IProtocolHandler> protocols)
            {
                _alphabetically = new (string, int)[protocols.Count];
                _alphabetically[0] = (Protocol.P2P, protocols[Protocol.P2P].MessageIdSpaceSize);
                int i = 1;
                foreach (KeyValuePair<string, IProtocolHandler> protocolSession
                    in protocols.Where(static kv => kv.Key != Protocol.P2P).OrderBy(static kv => kv.Key))
                {
                    _alphabetically[i++] = (protocolSession.Key, protocolSession.Value.MessageIdSpaceSize);
                }

                int size = 0;
                foreach ((string _, int spaceSize) in _alphabetically)
                {
                    size += spaceSize;
                }

                Size = Math.Clamp(size, 0, MaxAdaptiveIdCount);
            }

            public int Size { get; }

            public (string, int) ResolveProtocol(int adaptiveId)
            {
                (string ProtocolCode, int SpaceSize)[] alphabetically = _alphabetically;
                int offset = 0;
                for (int j = 0; j < alphabetically.Length; j++)
                {
                    ref readonly (string ProtocolCode, int SpaceSize) entry = ref alphabetically[j];
                    if (offset + entry.SpaceSize > adaptiveId)
                    {
                        return (entry.ProtocolCode, adaptiveId - offset);
                    }

                    offset += entry.SpaceSize;
                }

                // consider disconnecting on the breach of protocol here?
                return (null, 0);
            }

            public int ResolveAdaptiveId(string protocol, int messageCode)
            {
                (string ProtocolCode, int SpaceSize)[] alphabetically = _alphabetically;
                int offset = 0;
                for (int j = 0; j < alphabetically.Length; j++)
                {
                    ref readonly (string ProtocolCode, int SpaceSize) entry = ref alphabetically[j];
                    if (entry.ProtocolCode == protocol)
                    {
                        if (entry.SpaceSize <= messageCode)
                        {
                            break;
                        }

                        return offset + messageCode;
                    }

                    offset += entry.SpaceSize;
                }

                return ThrowUnsupportedProtocol(protocol, messageCode);

                [DoesNotReturn, StackTraceHidden]
                int ThrowUnsupportedProtocol(string proto, int msgCode)
                    => throw new InvalidOperationException(
                        $"Registered protocols do not support {proto} with message code {msgCode}. " +
                        $"Registered: {string.Join(";", _alphabetically)}.");
            }
        }

        private bool _isTracked = false;

        public void StartTrackingSession() => _isTracked = true;

        private static MessageRoute? GetIncomingRoute(MessageRoutes routes, int dynamicMessageCode)
        {
            MessageRoute?[] incoming = routes.Incoming;
            return (uint)dynamicMessageCode < (uint)incoming.Length && incoming[dynamicMessageCode] is { IsCurrent: true } route
                ? route
                : routes.CreateIncomingRoute(dynamicMessageCode);
        }

        private static MessageRoute GetOutgoingRoute(MessageRoutes routes, int adaptivePacketType, string protocol, int packetType)
        {
            MessageRoute?[] outgoing = routes.Outgoing;
            return (uint)adaptivePacketType < (uint)outgoing.Length
                && outgoing[adaptivePacketType] is { IsCurrent: true } route
                ? route
                : routes.CreateOutgoingRoute(adaptivePacketType, protocol, packetType);
        }

        private sealed class MessageRoutes(AdaptiveCodeResolver resolver, ConcurrentDictionary<string, IProtocolHandler> protocols)
        {
            public readonly AdaptiveCodeResolver Resolver = resolver;
            public readonly MessageRoute?[] Incoming = new MessageRoute?[resolver.Size];
            public readonly MessageRoute?[] Outgoing = new MessageRoute?[resolver.Size];

            [MethodImpl(MethodImplOptions.NoInlining)]
            public MessageRoute? CreateIncomingRoute(int dynamicMessageCode)
            {
                (string? protocol, int messageId) = Resolver.ResolveProtocol(dynamicMessageCode);
                if (protocol is null)
                {
                    return null;
                }

                protocols.TryGetValue(protocol, out IProtocolHandler? handler);
                MessageRoute route = new(protocol, messageId, handler, Metrics.IncomingP2PMessages, Metrics.IncomingP2PMessageBytes);
                if (handler is not null)
                {
                    Incoming[dynamicMessageCode] = route;
                }

                return route;
            }

            [MethodImpl(MethodImplOptions.NoInlining)]
            public MessageRoute CreateOutgoingRoute(int adaptivePacketType, string protocol, int packetType)
            {
                protocols.TryGetValue(protocol, out IProtocolHandler? handler);
                MessageRoute route = new(protocol, packetType, handler, Metrics.OutgoingP2PMessages, Metrics.OutgoingP2PMessageBytes);
                if (handler is not null && (uint)adaptivePacketType < (uint)Outgoing.Length)
                {
                    Outgoing[adaptivePacketType] = route;
                }

                return route;
            }
        }

        private sealed class MessageRoute
        {
            private readonly byte _version;
            private readonly MetricCounter _messages;
            private readonly MetricCounter _bytes;

            public MessageRoute(
                string protocol,
                int messageId,
                IProtocolHandler? handler,
                NonBlocking.ConcurrentDictionary<P2PMessageKey, MetricCounter> messages,
                NonBlocking.ConcurrentDictionary<P2PMessageKey, MetricCounter> bytes)
            {
                Protocol = protocol;
                MessageId = messageId;
                Handler = handler;
                _version = handler?.ProtocolVersion ?? 0;
                P2PMessageKey key = new(new VersionedProtocol(protocol, _version), messageId);
                _messages = messages.GetOrAdd(key, static _ => new MetricCounter());
                _bytes = bytes.GetOrAdd(key, static _ => new MetricCounter());
            }

            public string Protocol { get; }
            public int MessageId { get; }
            public IProtocolHandler? Handler { get; }
            public bool IsCurrent => Handler.ProtocolVersion == _version;

            public void Record(int size)
            {
                _messages.Increment();
                _bytes.Add(size);
            }
        }

        [DoesNotReturn, StackTraceHidden]
        private void ThrowInvalidSessionState([CallerMemberName] string caller = "")
            => throw new InvalidOperationException($"{caller} called on {this}");

        /// <summary>
        /// Lock-free dispatch collection for disconnect handlers. Remove uses swap-and-null,
        /// so handler invocation order is NOT guaranteed to match subscription order.
        /// </summary>
        private sealed class DisconnectEventHandlers
        {
            private readonly Lock _lock = new();
            private EventHandler<DisconnectEventArgs>?[] _handlers = [];
            private int _count;

            public bool HasHandlers
            {
                get { lock (_lock) { return _count != 0; } }
            }

            public void Add(EventHandler<DisconnectEventArgs> handler)
            {
                ArgumentNullException.ThrowIfNull(handler);

                lock (_lock)
                {
                    EventHandler<DisconnectEventArgs>?[] handlers = _handlers;
                    if (_count == handlers.Length)
                    {
                        Array.Resize(ref _handlers, handlers.Length == 0 ? 4 : handlers.Length * 2);
                    }

                    _handlers[_count++] = handler;
                }
            }

            public void Remove(EventHandler<DisconnectEventArgs> handler)
            {
                ArgumentNullException.ThrowIfNull(handler);

                lock (_lock)
                {
                    EventHandler<DisconnectEventArgs>?[] handlers = _handlers;
                    for (int i = _count - 1; i >= 0; i--)
                    {
                        if (handlers[i] != handler)
                        {
                            continue;
                        }

                        int last = --_count;
                        handlers[i] = handlers[last];
                        handlers[last] = null;
                        return;
                    }
                }
            }

            public void Invoke(object sender, DisconnectEventArgs args)
            {
                EventHandler<DisconnectEventArgs>?[] rentedHandlers;
                int count;
                lock (_lock)
                {
                    count = _count;
                    if (count == 0)
                    {
                        return;
                    }

                    rentedHandlers = ArrayPool<EventHandler<DisconnectEventArgs>?>.Shared.Rent(count);
                    Array.Copy(_handlers, rentedHandlers, count);
                }

                for (int i = 0; i < count; i++)
                {
                    rentedHandlers[i]!(sender, args);
                }
                Array.Clear(rentedHandlers, 0, count);
                ArrayPool<EventHandler<DisconnectEventArgs>?>.Shared.Return(rentedHandlers);
            }
        }
    }
}
