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
using System.Threading.Tasks;
using DotNetty.Transport.Channels;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Network.Rlpx;

namespace Nethermind.Network.P2P
{
    public class NettyP2PHandler : SimpleChannelInboundHandler<Packet>, IChannelController
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

        public void Init(IChannelHandlerContext context)
        {
            _context = context;
            _sessionManager.RegisterChannelController(this); // TODO: to be refactored
            _sessionManager.StartSession("p2p", 5, _packetSender, _remoteNodeId, _remotePort);
        }

        private IChannelHandlerContext _context;
        
        public override void ChannelRegistered(IChannelHandlerContext context)
        {
            _logger.Log($"Registering {nameof(IChannelController)}");
            
            _sessionManager.RegisterChannelController(this);
            base.ChannelRegistered(context);
        }

        protected override void ChannelRead0(IChannelHandlerContext ctx, Packet msg)
        {
            _logger.Log($"Channel read... data length {msg.Data.Length}");
            _sessionManager.ReceiveMessage(msg);
        }

        public override void ExceptionCaught(IChannelHandlerContext context, Exception exception)
        {   
            _logger.Error($"{nameof(NettyP2PHandler)} exception", exception);
            base.ExceptionCaught(context, exception);
        }

        public void EnableSnappy()
        {
            _logger.Error($"Enabling Snappy compression");
            _context.Channel.Pipeline.AddBefore($"{nameof(Multiplexor)}#0", null, new SnappyDecoder(_logger));
            _context.Channel.Pipeline.AddBefore($"{nameof(Multiplexor)}#0", null, new SnappyEncoder(_logger));
        }

        public void Disconnect(TimeSpan delay)
        {
            Task.Delay(delay).ContinueWith(t =>
            {
                _context.DisconnectAsync();
                _logger.Error($"Disconnecting now after {delay.TotalMilliseconds} milliseconds");
            });
        }
    }
}