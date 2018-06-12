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
using DotNetty.Transport.Channels;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Network.P2P.Subprotocols.Eth;
using Nethermind.Network.P2P.Subprotocols.Eth.V63;
using Nethermind.Network.Rlpx;

namespace Nethermind.Network.P2P
{
    public class P2PSession : IP2PSession
    {
        private readonly ILogger _logger;
        private readonly IMessageSerializationService _serializer;
        private readonly ISynchronizationManager _syncManager;
        private readonly Dictionary<string, IProtocolHandler> _protocols = new Dictionary<string, IProtocolHandler>();

        private IChannelHandlerContext _context;
        private IPacketSender _packetSender;
        
        private Func<int, (string, int)> _adaptiveCodeResolver;
        private Func<(string ProtocolCode, int PacketType), int> _adaptiveEncoder;
        
        public P2PSession(
            PublicKey localNodeId,
            int localPort,
            IMessageSerializationService serializer,
            ISynchronizationManager syncManager,
            ILogger logger)
        {
            _serializer = serializer;
            _syncManager = syncManager;
            _logger = logger;
            LocalNodeId = localNodeId;
            LocalPort = localPort;
            SessionId = Guid.NewGuid().ToString();
        }

        public PublicKey LocalNodeId { get; }
        public int LocalPort { get; }
        public PublicKey RemoteNodeId { get; set; }
        public int? RemotePort { get; set; }
        public string RemoteHost { get; set; }
        public ClientConnectionType ClientConnectionType { get; set; }
        public string SessionId { get; }

        // TODO: this should be one level up
        public void EnableSnappy()
        {
            _logger.Info($"{RemoteNodeId} Enabling Snappy compression");
            _context.Channel.Pipeline.AddBefore($"{nameof(Multiplexor)}#0", null, new SnappyDecoder(_logger));
            _context.Channel.Pipeline.AddBefore($"{nameof(Multiplexor)}#0", null, new SnappyEncoder(_logger));
        }

        public void Enqueue(Packet message, bool priority = false)
        {
            _packetSender.Enqueue(message, priority);
        }

        public void DeliverMessage(Packet packet, bool priority = false)
        {
            if (_logger.IsTraceEnabled)
            {
                _logger.Trace($"P2P to deliver {packet.Protocol}.{packet.PacketType} with payload {new Hex(packet.Data)}");
            }

            packet.PacketType = _adaptiveEncoder((packet.Protocol, packet.PacketType));
            _packetSender.Enqueue(packet, priority);
        }

        public void ReceiveMessage(Packet packet)
        {
            int dynamicMessageCode = packet.PacketType;
            (string protocol, int messageId) = ResolveMessageCode(dynamicMessageCode);
            packet.Protocol = protocol;

            if (_logger.IsDebugEnabled)
            {
                _logger.Debug($"{RemoteNodeId} {nameof(P2PSession)} received a message ({dynamicMessageCode} => {protocol}.{messageId})");
            }

            if (protocol == null)
            {
                _logger.Error($"{RemoteNodeId} {nameof(P2PSession)} received a message ({dynamicMessageCode} => {messageId})");
                _logger.Error($"{RemoteNodeId} Known protocols ({_protocols.Count}):");
                foreach (KeyValuePair<string, IProtocolHandler> protocolHandler in _protocols)
                {
                    _logger.Error($"{RemoteNodeId} {protocolHandler.Key} {protocolHandler.Value.ProtocolVersion} {protocolHandler.Value.MessageIdSpaceSize}");
                }

                throw new InvalidProtocolException(packet.Protocol);
            }

            packet.PacketType = messageId;
            _protocols[protocol].HandleMessage(packet);
        }

        // TODO: use custom interface instead of netty one (can encapsulate both)
        public void Init(byte p2PVersion, IChannelHandlerContext context, IPacketSender packetSender)
        {
            _packetSender = packetSender;
            _context = context;
            InitProtocol(Protocol.P2P, p2PVersion);
        }

        public async Task InitiateDisconnectAsync(DisconnectReason disconnectReason)
        {
            //Trigger disconnect on each protocol handler (if p2p is initialized it will send disconnect message to the peer)
            if (_protocols.Any())
            {
                foreach (var protocolHandler in _protocols.Values)
                {
                    protocolHandler.Disconnect(disconnectReason);
                }
            }
            await DisconnectAsync(disconnectReason, DisconnectType.Local);
        }

        public async Task DisconnectAsync(DisconnectReason disconnectReason, DisconnectType disconnectType, TimeSpan? delay = null)
        {
            if (!delay.HasValue)
            {
                //TODO move default delay time to configuration
                delay = new TimeSpan(0, 0, 0, 10);
            }

            if (PeerDisconnected != null)
            {
                PeerDisconnected.Invoke(this, new DisconnectEventArgs(disconnectReason, disconnectType, SessionId));
            }
            else
            {
                _logger.Info("No subscriptions for PeerDisconnected");
            }
            

            await Task.Delay(delay.Value).ContinueWith(t =>
            {
                _context.DisconnectAsync();
                _logger.Info($"{RemoteNodeId} Disconnecting now after {delay.Value.TotalMilliseconds} milliseconds");
            });
        }

        public event EventHandler<DisconnectEventArgs> PeerDisconnected;
        public event EventHandler<ProtocolInitializedEventArgs> ProtocolInitialized;

        private (string, int) ResolveMessageCode(int adaptiveId)
        {
            return _adaptiveCodeResolver.Invoke(adaptiveId);
        }

        private void InitProtocol(string protocolCode, int version)
        {
            protocolCode = protocolCode.ToLowerInvariant();
            if (_protocols.ContainsKey(protocolCode))
            {
                throw new InvalidOperationException($"{RemoteNodeId} Session for protocol {protocolCode} already started");
            }

            if (protocolCode != Protocol.P2P && !_protocols.ContainsKey(Protocol.P2P))
            {
                throw new InvalidOperationException($"{Protocol.P2P} protocolHandler has to be started before starting {protocolCode} protocolHandler");
            }

            IProtocolHandler protocolHandler;
            switch (protocolCode)
            {
                case Protocol.P2P:
                    protocolHandler = new P2PProtocolHandler(this, _serializer, LocalNodeId, LocalPort, _logger);
                    protocolHandler.ProtocolInitialized += (sender, args) =>
                    {
                        if (protocolHandler.ProtocolVersion >= 5)
                        {
                            _logger.Info($"{RemoteNodeId} {protocolHandler.ProtocolCode} v{protocolHandler.ProtocolVersion} established - Enabling Snappy");
                            EnableSnappy();
                        }
                        else
                        {
                            _logger.Info($"{RemoteNodeId} {protocolHandler.ProtocolCode} v{protocolHandler.ProtocolVersion} established - Disabling Snappy");
                        }
                        ProtocolInitialized?.Invoke(this, args);
                    };
                    break;
                case Protocol.Eth:
                    if (version < 62 || version > 63)
                    {
                        throw new NotSupportedException();
                    }

                    protocolHandler = version == 62
                        ? new Eth62ProtocolHandler(this, _serializer, _syncManager, _logger)
                        : new Eth63ProtocolHandler(this, _serializer, _syncManager, _logger);
                    protocolHandler.ProtocolInitialized += (sender, args) =>
                    {
                        //await _syncManager.AddPeer((Eth62ProtocolHandler)protocolHandler);
                        ProtocolInitialized?.Invoke(this, args);
                    };
                    break;
                default:
                    throw new NotSupportedException();
            }

            protocolHandler.SubprotocolRequested += (sender, args) => InitProtocol(args.ProtocolCode, args.Version);
            _protocols[protocolCode] = protocolHandler;

            (string ProtocolCode, int SpaceSize)[] alphabetically = new (string, int)[_protocols.Count];
            alphabetically[0] = (Protocol.P2P, _protocols[Protocol.P2P].MessageIdSpaceSize);
            int i = 1;
            foreach (KeyValuePair<string, IProtocolHandler> protocolSession in _protocols.Where(kv => kv.Key != "p2p").OrderBy(kv => kv.Key))
            {
                alphabetically[i++] = (protocolSession.Key, protocolSession.Value.MessageIdSpaceSize);
            }

            _adaptiveCodeResolver = dynamicId =>
            {
                int offset = 0;
                for (int j = 0; j < alphabetically.Length; j++)
                {
                    if (offset + alphabetically[j].SpaceSize > dynamicId)
                    {
                        return (alphabetically[j].ProtocolCode, dynamicId - offset);
                    }

                    offset += alphabetically[j].SpaceSize;
                }

                _logger.Warn($"Could not resolve message id from {dynamicId} with known:");
                for (int j = 0; j < alphabetically.Length; j++)
                {
                    _logger.Warn($"{alphabetically[j].ProtocolCode} {alphabetically[j].SpaceSize}");
                }

                return (null, 0);
            };

            _adaptiveEncoder = args =>
            {
                int offset = 0;
                for (int j = 0; j < alphabetically.Length; j++)
                {
                    if (alphabetically[j].ProtocolCode == args.ProtocolCode)
                    {
                        return offset + args.PacketType;
                    }

                    offset += alphabetically[j].SpaceSize;
                }

                return args.PacketType;
            };

            protocolHandler.Init();
        }

        private void CloseSession(string protocolCode)
        {
            throw new NotImplementedException();
        }      
    }
}