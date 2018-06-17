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
using System.Net;
using System.Threading.Tasks;
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

        private readonly IEncryptionHandshakeService _service;
        private readonly IP2PSession _ip2PSession;
        private PublicKey _remoteId;

        public NettyHandshakeHandler(
            IEncryptionHandshakeService service,
            IP2PSession ip2PSession,
            EncryptionHandshakeRole role,
            PublicKey remoteId,
            ILogger logger)
        {
            _handshake.RemotePublicKey = remoteId;
            _role = role;
            _remoteId = remoteId;
            _logger = logger;
            _service = service;
            _ip2PSession = ip2PSession;
        }

        public override void ChannelActive(IChannelHandlerContext context)
        {
            if (_role == EncryptionHandshakeRole.Initiator)
            {
                Packet auth = _service.Auth(_remoteId, _handshake);

                if (_logger.IsDebugEnabled) _logger.Debug($"Sending AUTH to {_remoteId} @ {context.Channel.RemoteAddress}");
                _buffer.WriteBytes(auth.Data);
                context.WriteAndFlushAsync(_buffer);
            }
        }

        public override void ChannelInactive(IChannelHandlerContext context)
        {
            if (_logger.IsDebugEnabled) _logger.Debug("Channel Inactive");
            base.ChannelInactive(context);
        }

        public override Task DisconnectAsync(IChannelHandlerContext context)
        {
            if (_logger.IsDebugEnabled) _logger.Debug("Disconnected");
            return base.DisconnectAsync(context);
        }

        public override void ChannelUnregistered(IChannelHandlerContext context)
        {
            if (_logger.IsDebugEnabled) _logger.Debug("Channel Unregistered");
            base.ChannelUnregistered(context);
        }

        public override void ChannelRegistered(IChannelHandlerContext context)
        {
            if (_logger.IsDebugEnabled)  _logger.Debug("Channel Registered");
            base.ChannelRegistered(context);
        }

        public override void ExceptionCaught(IChannelHandlerContext context, Exception exception)
        {
            if (_logger.IsErrorEnabled) _logger.Error("Exception when processing encryption handshake", exception);
            base.ExceptionCaught(context, exception);
        }

        public override void ChannelReadComplete(IChannelHandlerContext context)
        {
            context.Flush();
        }

        public override void ChannelRead(IChannelHandlerContext context, object message)
        {
            _logger.Trace($"Channel read {nameof(NettyHandshakeHandler)} from {context.Channel.RemoteAddress}");
            if (message is IByteBuffer byteBuffer)
            {
                if (_role == EncryptionHandshakeRole.Recipient)
                {
                    if (_logger.IsDebugEnabled) _logger.Debug($"AUTH received from {context.Channel.RemoteAddress}");
                    byte[] authData = new byte[byteBuffer.ReadableBytes];
                    byteBuffer.ReadBytes(authData);
                    Packet ack = _service.Ack(_handshake, new Packet(authData));
                    _remoteId = _handshake.RemotePublicKey;

                    if (_logger.IsDebugEnabled) _logger.Debug($"Sending ACK to {_remoteId} @ {context.Channel.RemoteAddress}");
                    _buffer.WriteBytes(ack.Data);
                    context.WriteAndFlushAsync(_buffer);
                }
                else
                {
                    if (_logger.IsDebugEnabled) _logger.Debug($"Received ACK from {_remoteId} @ {context.Channel.RemoteAddress}");
                    byte[] ackData = new byte[byteBuffer.ReadableBytes];
                    byteBuffer.ReadBytes(ackData);
                    _service.Agree(_handshake, new Packet(ackData));
                }

                _ip2PSession.RemoteNodeId = _handshake.RemotePublicKey;
                HandshakeInitialized?.Invoke(this, new HandshakeInitializedEventArgs());

                FrameCipher frameCipher = new FrameCipher(_handshake.Secrets.AesSecret);
                FrameMacProcessor macProcessor = new FrameMacProcessor(_handshake.Secrets);

                if (_logger.IsTraceEnabled) _logger.Trace($"Registering {nameof(NettyFrameDecoder)} for {_remoteId} @ {context.Channel.RemoteAddress}");
                context.Channel.Pipeline.AddLast(new NettyFrameDecoder(frameCipher, macProcessor, _logger));
                if (_logger.IsTraceEnabled) _logger.Trace($"Registering {nameof(NettyFrameEncoder)} for {_remoteId} @ {context.Channel.RemoteAddress}");
                context.Channel.Pipeline.AddLast(new NettyFrameEncoder(frameCipher, macProcessor, _logger));
                if (_logger.IsTraceEnabled) _logger.Trace($"Registering {nameof(NettyFrameMerger)} for {_remoteId} @ {context.Channel.RemoteAddress}");
                context.Channel.Pipeline.AddLast(new NettyFrameMerger(_logger));
                if (_logger.IsTraceEnabled) _logger.Trace($"Registering {nameof(NettyPacketSplitter)} for {_remoteId} @ {context.Channel.RemoteAddress}");
                context.Channel.Pipeline.AddLast(new NettyPacketSplitter());

                Multiplexor multiplexor = new Multiplexor(_logger);
                if (_logger.IsTraceEnabled) _logger.Trace($"Registering {nameof(Multiplexor)} for {_ip2PSession.RemoteNodeId} @ {context.Channel.RemoteAddress}");
                context.Channel.Pipeline.AddLast(multiplexor);

                if (_logger.IsTraceEnabled) _logger.Trace($"Registering {nameof(NettyP2PHandler)} for {_remoteId} @ {context.Channel.RemoteAddress}");
                NettyP2PHandler handler = new NettyP2PHandler(_ip2PSession, _logger);
                context.Channel.Pipeline.AddLast(handler);

                handler.Init(multiplexor, context);

                if (_logger.IsTraceEnabled) _logger.Trace($"Removing {nameof(NettyHandshakeHandler)}");
                context.Channel.Pipeline.Remove(this);
                if (_logger.IsTraceEnabled) _logger.Trace($"Removing {nameof(LengthFieldBasedFrameDecoder)}");
                context.Channel.Pipeline.Remove<LengthFieldBasedFrameDecoder>();
            }
            else
            {
                if (_logger.IsErrorEnabled) _logger.Error($"DIFFERENT TYPE OF DATA {message.GetType()}");
            }
        }

        public override void HandlerRemoved(IChannelHandlerContext context)
        {
            if (_logger.IsDebugEnabled) _logger.Debug($"Handshake with {_remoteId} @ {context.Channel.RemoteAddress} finished. Removing {nameof(NettyHandshakeHandler)} from the pipeline");
        }

        public event EventHandler<HandshakeInitializedEventArgs> HandshakeInitialized;
    }
}