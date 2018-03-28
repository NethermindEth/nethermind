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
using Nethermind.Core.Crypto;
using Nethermind.Network.Rlpx;

namespace Nethermind.Network.P2P
{
    public class NettyP2PHandler : SimpleChannelInboundHandler<Packet>
    {
        public static byte Version = 5; // TODO: move somewhere else

        private readonly ISessionManager _sessionManager;
        private readonly IPacketSender _packetSender;
        private readonly ILogger _logger;
        private readonly PublicKey _remoteNodeId;
        private readonly int _remotePort;

        public NettyP2PHandler(ISessionManager sessionManager, IPacketSender packetSender, ILogger logger, PublicKey remoteNodeId, int remotePort)
        {
            _sessionManager = sessionManager;
            _packetSender = packetSender;
            _logger = logger;
            _remoteNodeId = remoteNodeId;
            _remotePort = remotePort;
        }

        public void Init()
        {
            _sessionManager.Start(0, 5, _packetSender, _remoteNodeId, _remotePort);
        }
        
        protected override void ChannelRead0(IChannelHandlerContext ctx, Packet msg)
        {
            _sessionManager.DeliverMessage(msg);
        }

        public override void ExceptionCaught(IChannelHandlerContext context, Exception exception)
        {
            _logger.Error($"{nameof(NettyP2PHandler)} exception", exception);
            base.ExceptionCaught(context, exception);
        }
    }
}