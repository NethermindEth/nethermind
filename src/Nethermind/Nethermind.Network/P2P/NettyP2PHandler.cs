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
using DotNetty.Transport.Channels;
using Nethermind.Core;
using Nethermind.Network.Rlpx;

namespace Nethermind.Network.P2P
{
    public class NettyP2PHandler : SimpleChannelInboundHandler<Packet>
    {
        public static byte Version = 5; // TODO: move somewhere else
        
        private readonly ILogger _logger;
        private readonly IMessageSerializationService _serializationService;
        private readonly ISession _session;

        public NettyP2PHandler(ISession session, IMessageSerializationService serializationService, ILogger logger)
        {
            _session = session;
            _serializationService = serializationService;
            _logger = logger;
        }

        protected override void ChannelRead0(IChannelHandlerContext ctx, Packet msg)
        {
            if (msg.PacketType == P2PMessageCode.Hello)
            {
                HelloMessage helloMessage = _serializationService.Deserialize<HelloMessage>(msg.Data);
                _logger.Log($"Received hello from {helloMessage.NodeId} @ {ctx.Channel.RemoteAddress} ({helloMessage.ClientId})");
                _session.InitInbound(helloMessage);
            }
            else if (msg.PacketType == P2PMessageCode.Disconnect)
            {
                DisconnectMessage disconnectMessage = _serializationService.Deserialize<DisconnectMessage>(msg.Data);
                _logger.Log($"Received disconnect ({disconnectMessage.Reason}) from {ctx.Channel.RemoteAddress}");
                _session.Close(disconnectMessage.Reason);
            }
            else if (msg.PacketType == P2PMessageCode.Ping)
            {
                _logger.Log($"Received PING from {ctx.Channel.RemoteAddress}");
                _session.HandlePing();
            }
            else if (msg.PacketType == P2PMessageCode.Pong)
            {
                _logger.Log($"Received PONG from {ctx.Channel.RemoteAddress}");
                _session.HandlePong();
            }
            else
            {
                _logger.Error($"Unhandled packet type: {msg.PacketType}");
            }
        }

        public override void ExceptionCaught(IChannelHandlerContext context, Exception exception)
        {
            _logger.Error($"{nameof(NettyP2PHandler)} exception", exception);
            base.ExceptionCaught(context, exception);
        }
    }
}