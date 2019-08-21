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
using DotNetty.Buffers;
using DotNetty.Codecs;
using DotNetty.Transport.Channels;
using Nethermind.Logging;
using Nethermind.Network.P2P;

namespace Nethermind.Network.Rlpx
{
    public class MessageSerializingEncoder : MessageToByteEncoder<P2PMessage>
    {
        private readonly IMessageSerializationService _messageSerializationService;
        private readonly ILogger _logger;

        public MessageSerializingEncoder(IMessageSerializationService messageSerializationService, ILogManager logManager)
        {
            _messageSerializationService = messageSerializationService ?? throw new ArgumentNullException(nameof(messageSerializationService));
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
        }
        
        protected override void Encode(IChannelHandlerContext context, P2PMessage input, IByteBuffer output)
        {
            output.WriteByte(input.PacketType);
            _messageSerializationService.Serialize(input, output);
        }
    }
}