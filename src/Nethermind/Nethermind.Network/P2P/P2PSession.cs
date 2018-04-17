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
        
        public PublicKey LocalNodeId { get; }
        public int LocalPort { get; }
        
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
        }

        public PublicKey RemoteNodeId { get; set; }
        public int RemotePort { get; set; }

        // TODO: this should be one level up
        public void EnableSnappy()
        {
            _logger.Info($"Enabling Snappy compression");
            _context.Channel.Pipeline.AddBefore($"{nameof(Multiplexor)}#0", null, new SnappyDecoder(_logger));
            _context.Channel.Pipeline.AddBefore($"{nameof(Multiplexor)}#0", null, new SnappyEncoder(_logger));
        }

        // TODO: this should be one level up
        public void Disconnect(TimeSpan delay)
        {
            Task.Delay(delay).ContinueWith(t =>
            {
                _context.DisconnectAsync();
                _logger.Info($"Disconnecting now after {delay.TotalMilliseconds} milliseconds");
            });
        }

        public void Enqueue(Packet message, bool priority = false)
        {
            _packetSender.Enqueue(message, priority);
        }

        public void DeliverMessage(Packet packet, bool priority = false)
        {
            packet.PacketType = _adaptiveEncoder((packet.Protocol, packet.PacketType));
            _packetSender.Enqueue(packet, priority);
        }

        private (string, int) ResolveMessageCode(int adaptiveId)
        {
            return _adaptiveCodeResolver.Invoke(adaptiveId);
        }

        public void ReceiveMessage(Packet packet)
        {
            int dynamicMessageCode = packet.PacketType;
            (string protocol, int messageId) = ResolveMessageCode(dynamicMessageCode);
            packet.Protocol = protocol;

            _logger.Info($"Session Manager received a message (dynamic ID {dynamicMessageCode}. Resolved to {protocol}.{messageId})");

            if (protocol == null)
            {
                throw new InvalidProtocolException(packet.Protocol);
            }

            packet.PacketType = messageId;
            _protocols[protocol].HandleMessage(packet);
        }

        private void InitProtocol(string protocolCode, int version)
        {
            protocolCode = protocolCode.ToLowerInvariant();
            if (_protocols.ContainsKey(protocolCode))
            {
                throw new InvalidOperationException($"Session for protocol {protocolCode} already started");
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
                            _logger.Info($"{protocolHandler.ProtocolCode} v{protocolHandler.ProtocolVersion} established - Enabling Snappy");
                            EnableSnappy();
                        }
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
                    protocolHandler.ProtocolInitialized += async (sender, args) =>
                    {
                        await _syncManager.AddPeer((Eth62ProtocolHandler)protocolHandler);
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

        // TODO: use custome interface instead of netty one (can encapsulate both)
        public void Init(byte p2PVersion, IChannelHandlerContext context, IPacketSender packetSender)
        {
            _packetSender = packetSender;
            _context = context;
            InitProtocol(Protocol.P2P, p2PVersion);
        }
    }
}