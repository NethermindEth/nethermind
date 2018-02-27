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
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Network.P2P;
using Nethermind.Network.Rlpx.Handshake;

namespace Nethermind.Network.Rlpx
{
    public class NettyHandshakeHandler : ChannelHandlerAdapter
    {
        private readonly IByteBuffer _buffer = Unpooled.Buffer(256); // TODO: analyze buffer size effect
        private readonly EncryptionHandshake _handshake = new EncryptionHandshake();
        private readonly ILogger _logger;
        private readonly EncryptionHandshakeRole _role;
        private readonly IMessageSerializationService _serializationService;

        private readonly IEncryptionHandshakeService _service;
        private readonly ISessionFactory _sessionFactory;
        private PublicKey _remoteId;

        public NettyHandshakeHandler(
            IEncryptionHandshakeService service,
            ISessionFactory sessionFactory,
            IMessageSerializationService serializationService,
            EncryptionHandshakeRole role,
            PublicKey remoteId,
            ILogger logger)
        {
            _handshake.RemotePublicKey = remoteId;
            _role = role;
            _remoteId = remoteId;
            _logger = logger;
            _service = service;
            _sessionFactory = sessionFactory;

            _serializationService = serializationService;
        }

        public override void ChannelActive(IChannelHandlerContext context)
        {
            if (_role == EncryptionHandshakeRole.Initiator)
            {
                Packet auth = _service.Auth(_remoteId, _handshake);

                _logger.Log($"Sending AUTH to {_remoteId} @ {context.Channel.RemoteAddress}");
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
                    byte[] authData = new byte[byteBuffer.ReadableBytes];
                    byteBuffer.ReadBytes(authData);
                    Packet ack = _service.Ack(_handshake, new Packet(authData));
                    _remoteId = _handshake.RemotePublicKey;

                    _logger.Log($"Sending ACK to {_remoteId} @ {context.Channel.RemoteAddress}");
                    _buffer.WriteBytes(ack.Data);
                    context.WriteAndFlushAsync(_buffer);
                }
                else
                {
                    _logger.Log($"Received ACK from {_remoteId} @ {context.Channel.RemoteAddress}");
                    byte[] ackData = new byte[byteBuffer.ReadableBytes];
                    byteBuffer.ReadBytes(ackData);
                    _service.Agree(_handshake, new Packet(ackData));
                }

                FrameCipher frameCipher = new FrameCipher(_handshake.Secrets.AesSecret);
                FrameMacProcessor macProcessor = new FrameMacProcessor(_handshake.Secrets);

                context.Channel.Pipeline.Remove(this);
                context.Channel.Pipeline.Remove<LengthFieldBasedFrameDecoder>();

                // TODO: base class for Netty handlers and codecs with instrumentations?
                _logger.Log($"Registering {nameof(NettyFrameDecoder)} for {_remoteId} @ {context.Channel.RemoteAddress}");
                context.Channel.Pipeline.AddLast(new NettyFrameDecoder(frameCipher, macProcessor, _logger));
                _logger.Log($"Registering {nameof(NettyFrameEncoder)} for {_remoteId} @ {context.Channel.RemoteAddress}");
                context.Channel.Pipeline.AddLast(new NettyFrameEncoder(frameCipher, macProcessor));
                _logger.Log($"Registering {nameof(NettyFrameMerger)} for {_remoteId} @ {context.Channel.RemoteAddress}");
                context.Channel.Pipeline.AddLast(new NettyFrameMerger(_logger));
                _logger.Log($"Registering {nameof(NettyPacketSplitter)} for {_remoteId} @ {context.Channel.RemoteAddress}");
                context.Channel.Pipeline.AddLast(new NettyPacketSplitter());

                Multiplexor multiplexor = new Multiplexor(_serializationService, _logger);
                ISession session = _sessionFactory.Create(multiplexor);
                _logger.Log($"Registering {nameof(NettyP2PHandler)} for {_remoteId} @ {context.Channel.RemoteAddress}");
                context.Channel.Pipeline.AddLast(new NettyP2PHandler(session, _serializationService, _logger));
                _logger.Log($"Registering {nameof(Multiplexor)} for {_remoteId} @ {context.Channel.RemoteAddress}");
                context.Channel.Pipeline.AddLast(multiplexor);
                session.InitOutbound();
                session.Ping();
                session.Disconnect(DisconnectReason.ClientQuitting);
            }
            else
            {
                Debug.Assert(false, $"Always expecting {nameof(IByteBuffer)} as an input to {nameof(NettyHandshakeHandler)}");
            }
        }

        public override void HandlerRemoved(IChannelHandlerContext context)
        {
            _logger.Log($"Handshake complete. Removing {nameof(NettyHandshakeHandler)} for {_remoteId} @ {context.Channel.RemoteAddress} from the pipeline");
        }
    }
}