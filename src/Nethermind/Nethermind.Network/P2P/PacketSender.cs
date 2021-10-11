//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System;
using DotNetty.Buffers;
using DotNetty.Transport.Channels;
using Nethermind.Logging;

namespace Nethermind.Network.P2P
{
    public class PacketSender : ChannelHandlerAdapter, IPacketSender
    {
        private readonly IMessageSerializationService _messageSerializationService;
        private readonly ILogger _logger;
        private IChannelHandlerContext _context;

        public PacketSender(IMessageSerializationService messageSerializationService, ILogManager logManager)
        {
            _messageSerializationService = messageSerializationService ?? throw new ArgumentNullException(nameof(messageSerializationService));
            _logger = logManager.GetClassLogger<PacketSender>() ?? throw new ArgumentNullException(nameof(logManager));
        }

        public int Enqueue<T>(T message) where T : P2PMessage
        {
            if (!_context.Channel.Active)
            {
                return 0;
            }
            
            IByteBuffer buffer = _messageSerializationService.ZeroSerialize(message);
            var length = buffer.ReadableBytes;
            _context.WriteAndFlushAsync(buffer).ContinueWith(t =>
            {
                if (t.IsFaulted)
                {
                    if (_context.Channel != null && !_context.Channel.Active)
                    {
                        if (_logger.IsTrace) _logger.Trace($"Channel is not active - {t.Exception.Message}");
                    }
                    else if (_logger.IsError) _logger.Error("Channel is active", t.Exception);
                }
                else if (t.IsCompleted)
                {
//                    if (_logger.IsTrace) _logger.Trace($"Packet ({packet.Protocol}.{packet.PacketType}) pushed");
                }
            });
            
            return length;
        }

        public override void HandlerAdded(IChannelHandlerContext context)
        {
            _context = context;
        }
    }
}
