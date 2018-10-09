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
using System.Net.Sockets;
using DotNetty.Transport.Channels;
using Nethermind.Core.Logging;
using Nethermind.Network.Rlpx;

namespace Nethermind.Network.P2P
{
    public class NettyP2PHandler : SimpleChannelInboundHandler<Packet>
    {
        private readonly IP2PSession _ip2PSession;
        private readonly ILogger _logger;

        public NettyP2PHandler(IP2PSession ip2PSession, ILogger logger)
        {
            _ip2PSession = ip2PSession;
            _logger = logger;
        }

        public void Init(IPacketSender packetSender, IChannelHandlerContext context)
        {
            _ip2PSession.Init(5, context, packetSender);
        }

        public override void ChannelRegistered(IChannelHandlerContext context)
        {
            if (_logger.IsDebug) _logger.Debug($"Registering {nameof(NettyP2PHandler)}");
            base.ChannelRegistered(context);
        }

        protected override void ChannelRead0(IChannelHandlerContext ctx, Packet msg)
        {
            if (_logger.IsTrace) _logger.Trace($"Channel read... data length {msg.Data.Length}");
            _ip2PSession.ReceiveMessage(msg);
        }

        public override void ExceptionCaught(IChannelHandlerContext context, Exception exception)
        {
            //In case of SocketException we log it as debug to avoid noise
            if (exception is SocketException)
            {
                if (_logger.IsTrace) _logger.Trace($"NettyP2PHandler error in p2p netty handler (SocketException): {exception}");
            }
            else
            {
                if (_logger.IsDebug) _logger.Debug($"{GetType().Name} error in p2p netty handler: {exception}");
            } 

            base.ExceptionCaught(context, exception);
        }
    }
}