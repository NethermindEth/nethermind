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
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DotNetty.Transport.Channels;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Logging;
using Nethermind.Network.Rlpx;
using Nethermind.Stats.Model;

namespace Nethermind.Network.P2P
{
    public class Session : ISession
    {
        private readonly ILogger _logger;
        private readonly Dictionary<string, IProtocolHandler> _protocols = new Dictionary<string, IProtocolHandler>();
        private readonly IChannel _channel;
        private IChannelHandlerContext _context;

        private Node _node;

        public Session(
            int localPort,
            ILogManager logManager,
            IChannel channel)
        {
            Direction = ConnectionDirection.In;
            State = SessionState.New;
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
                    throw new InvalidOperationException($"{nameof(EnableSnappy)} called on session that is in the {State} state");
                }

                if (IsClosing)
                {
                    return;
                }
            }

            if (_logger.IsTrace) _logger.Trace($"{RemoteNodeId} Enabling Snappy compression and disabling framing");
            _context.Channel.Pipeline.Get<NettyPacketSplitter>().DisableFraming();
            _context.Channel.Pipeline.AddBefore($"{nameof(PacketSender)}#0", null, new SnappyDecoder(_logger));
            _context.Channel.Pipeline.AddBefore($"{nameof(PacketSender)}#0", null, new SnappyEncoder(_logger));
        }

        public IPingSender PingSender { get; set; }

        public void DeliverMessage(Packet packet)
        {
            lock (_sessionStateLock)
            {
                if (State < SessionState.Initialized)
                {
                    throw new InvalidOperationException($"{nameof(DeliverMessage)} called on session that is in the {State} state");
                }

                if (IsClosing)
                {
                    return;
                }
            }

            if (_logger.IsTrace) _logger.Trace($"P2P to deliver {packet.Protocol}.{packet.PacketType} with payload {packet.Data.ToHexString()}");

            packet.PacketType = _resolver.ResolveAdaptiveId(packet.Protocol, packet.PacketType);
            _packetSender.Enqueue(packet);
        }

        public void ReceiveMessage(Packet packet)
        {
            lock (_sessionStateLock)
            {
                if (State < SessionState.Initialized)
                {
                    throw new InvalidOperationException($"{nameof(ReceiveMessage)} called on session that is in the {State} state");
                }

                if (IsClosing)
                {
                    return;
                }
            }

            int dynamicMessageCode = packet.PacketType;
            (string protocol, int messageId) = _resolver.ResolveProtocol(packet.PacketType);
            packet.Protocol = protocol;

            if (_logger.IsTrace) _logger.Trace($"{RemoteNodeId} {nameof(Session)} received a message of length {packet.Data.Length} ({dynamicMessageCode} => {protocol}.{messageId})");

            if (protocol == null)
            {
                if (_logger.IsTrace) _logger.Warn($"Received a message from node: {RemoteNodeId}, ({dynamicMessageCode} => {messageId}), known protocols ({_protocols.Count}): {string.Join(", ", _protocols.Select(x => $"{x.Key} {x.Value.ProtocolVersion} {x.Value.MessageIdSpaceSize}"))}");
                return;
            }

            packet.PacketType = messageId;
            _protocols[protocol].HandleMessage(packet);
        }

        public void Init(byte p2PVersion, IChannelHandlerContext context, IPacketSender packetSender)
        {
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
                    throw new InvalidOperationException($"{nameof(Init)} called on session that is not in the {nameof(HandshakeComplete)} state");
                }

                _context = context;
                _packetSender = packetSender;
                State = SessionState.Initialized;
            }

            Initialized?.Invoke(this, EventArgs.Empty);
        }

        public void Handshake(PublicKey handshakeRemoteNodeId)
        {
            lock (_sessionStateLock)
            {
                if (State == SessionState.Initialized || State == SessionState.HandshakeComplete)
                {
                    throw new InvalidOperationException($"{nameof(Handshake)} called on session that is in the {State} state");
                }
                
                if (IsClosing)
                {
                    return;
                }

                State = SessionState.HandshakeComplete;
            }

            //For IN connections we don't have NodeId until that moment, so we need to set it in Session
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

        public void InitiateDisconnect(DisconnectReason disconnectReason)
        {
            lock (_sessionStateLock)
            {
                if (IsClosing)
                {
                    return;
                }

                State = SessionState.DisconnectingProtocols;
            }

            //Trigger disconnect on each protocol handler (if p2p is initialized it will send disconnect message to the peer)
            if (_protocols.Any())
            {
                foreach (var protocolHandler in _protocols.Values)
                {
                    try
                    {
                        protocolHandler.InitiateDisconnect(disconnectReason);
                    }
                    catch (Exception e)
                    {
                        if(_logger.IsDebug) _logger.Error($"DEBUG/ERROR Failed to disconnect {protocolHandler.ProtocolCode} {protocolHandler.ProtocolVersion} correctly", e);
                    }
                }
            }

            Disconnect(disconnectReason, DisconnectType.Local);
        }

        private object _sessionStateLock = new object();
        public byte P2PVersion { get; private set; }
        public SessionState State { get; private set; }

        public void Disconnect(DisconnectReason disconnectReason, DisconnectType disconnectType)
        {
            lock (_sessionStateLock)
            {
                if (State >= SessionState.Disconnecting)
                {
                    return;
                }

                State = SessionState.Disconnecting;
            }

            Disconnecting?.Invoke(this, new DisconnectEventArgs(disconnectReason, DisconnectType.Local));

            //Possible in case of disconnect before p2p initialization
            if (_context == null)
            {
                //in case pipeline did not get to p2p - no disconnect delay
                _channel.DisconnectAsync().ContinueWith(x =>
                {
                    if (x.IsFaulted && _logger.IsTrace) _logger.Trace($"Error while disconnecting on channel: {x.Exception}");
                });
            }
            else
            {
                var delayTask = disconnectType == DisconnectType.Local ? Task.Delay(Timeouts.Disconnection) : Task.CompletedTask;
                delayTask.ContinueWith(t =>
                {
                    _context.DisconnectAsync().ContinueWith(x =>
                    {
                        if (x.IsFaulted && _logger.IsTrace) _logger.Trace($"Error while disconnecting on context: {x.Exception}");
                    });
                    if (_logger.IsTrace) _logger.Trace($"{RemoteNodeId} Disconnecting now after {Timeouts.Disconnection.TotalMilliseconds} milliseconds");
                });
            }

            lock (_sessionStateLock)
            {
                State = SessionState.Disconnected;
            }

            if (Disconnected != null)
            {
                Disconnected?.Invoke(this, new DisconnectEventArgs(disconnectReason, disconnectType));
            }
            else if (_logger.IsDebug) _logger.Error($"DEBUG/ERROR  No subscriptions for session disconnected for {RemoteNodeId}");
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
                    throw new InvalidOperationException("Disposing session that is not disconnected");
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
                throw new InvalidOperationException($"{RemoteNodeId} Session for protocol {handler.ProtocolCode} already started");
            }

            if (handler.ProtocolCode != Protocol.P2P && !_protocols.ContainsKey(Protocol.P2P))
            {
                throw new InvalidOperationException($"{Protocol.P2P} protocolHandler has to be started before starting {handler.ProtocolCode} protocolHandler");
            }

            _protocols.Add(handler.ProtocolCode, handler);
            _resolver = GetOrCreateResolver();
        }

        private AdaptiveCodeResolver _resolver;

        private AdaptiveCodeResolver GetOrCreateResolver()
        {
            string key = string.Join(":", _protocols.Select(p => p.Key).OrderBy(x => x).ToArray());
            if (!_resolvers.ContainsKey(key))
            {
                _resolvers[key] = new AdaptiveCodeResolver(_protocols);
            }

            return _resolvers[key];
        }

        private static ConcurrentDictionary<string, AdaptiveCodeResolver> _resolvers = new ConcurrentDictionary<string, AdaptiveCodeResolver>();

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