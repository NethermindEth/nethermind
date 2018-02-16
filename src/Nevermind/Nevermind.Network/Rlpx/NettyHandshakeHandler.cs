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
using System.Diagnostics;
using DotNetty.Buffers;
using DotNetty.Codecs;
using DotNetty.Transport.Channels;
using Nevermind.Core;
using Nevermind.Core.Crypto;
using Nevermind.Network.Rlpx.Handshake;

namespace Nevermind.Network.Rlpx
{
    public class NettyHandshakeHandler : ChannelHandlerAdapter
    {
        private readonly IByteBuffer _buffer = Unpooled.Buffer(256); // TODO: analyze buffer size effect
        private readonly EncryptionHandshake _handshake = new EncryptionHandshake();
        private readonly ILogger _logger;
        private readonly PublicKey _remoteId;
        private readonly EncryptionHandshakeRole _role;

        private readonly IEncryptionHandshakeService _service;

        public NettyHandshakeHandler(IEncryptionHandshakeService service, EncryptionHandshakeRole role, PublicKey remoteId, ILogger logger)
        {
            _handshake.RemotePublicKey = remoteId;
            _role = role;
            _remoteId = remoteId;
            _logger = logger;
            _service = service;
        }

        public override void ChannelActive(IChannelHandlerContext context)
        {
            if (_role == EncryptionHandshakeRole.Initiator)
            {
                Packet auth = _service.Auth(_remoteId, _handshake);

                _logger.Log($"Sending AUTH to {_remoteId}");
                _buffer.WriteBytes(auth.Data);
                context.WriteAndFlushAsync(_buffer);
            }
        }

        public override void ExceptionCaught(IChannelHandlerContext context, Exception exception)
        {
            _logger.Error("Exception when processing encryption handshake", exception);
            base.ExceptionCaught(context, exception);
        }

        public override void ChannelReadComplete(IChannelHandlerContext context)
        {
            context.Flush();
        }

        public override void ChannelRead(IChannelHandlerContext context, object message)
        {
            if (message is IByteBuffer byteBuffer)
            {
                if (_role == EncryptionHandshakeRole.Recipient)
                {
                    _logger.Log($"AUTH received from {context.Channel.RemoteAddress}");
                    byte[] authData = new byte[byteBuffer.MaxCapacity];
                    byteBuffer.ReadBytes(authData);
                    Packet ack = _service.Ack(_handshake, new Packet(authData));

                    _logger.Log($"Sending ACK to {context.Channel.RemoteAddress}");
                    _buffer.WriteBytes(ack.Data);
                    context.WriteAndFlushAsync(_buffer);
                }
                else
                {
                    _logger.Log($"Received ACK from {context.Channel.RemoteAddress}");
                    byte[] ackData = new byte[byteBuffer.MaxCapacity];
                    byteBuffer.ReadBytes(ackData);
                    _service.Agree(_handshake, new Packet(ackData));
                    // TODO: clear pipeline, initiate protocol handshake (P2P)
                }
                
                FrameCipher frameCipher = new FrameCipher(_handshake.Secrets.AesSecret);
                FrameMacProcessor macProcessor = new FrameMacProcessor(_handshake.Secrets); 
                
                context.Channel.Pipeline.Remove(this);
                context.Channel.Pipeline.Remove<LengthFieldBasedFrameDecoder>();
                context.Channel.Pipeline.AddLast(new NettyFrameDecoder(frameCipher, macProcessor));
                context.Channel.Pipeline.AddLast(new NettyFrameEncoder(frameCipher, macProcessor));
                context.Channel.Pipeline.AddLast(new NettyFrameMerger());
                context.Channel.Pipeline.AddLast(new NettyPacketSplitter());
            }
            else
            {
                Debug.Assert(false, $"Always expecting {nameof(IByteBuffer)} as an input to {nameof(NettyHandshakeHandler)}");
            }
        }
    }
}