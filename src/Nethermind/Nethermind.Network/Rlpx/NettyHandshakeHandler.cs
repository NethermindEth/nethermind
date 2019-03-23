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
using System.Net.Sockets;
using System.Threading.Tasks;
using DotNetty.Buffers;
using DotNetty.Codecs;
using DotNetty.Common.Concurrency;
using DotNetty.Transport.Channels;
using Nethermind.Core.Crypto;
using Nethermind.Core.Logging;
using Nethermind.Network.P2P;
using Nethermind.Network.Rlpx.Handshake;

namespace Nethermind.Network.Rlpx
{
    public class NettyHandshakeHandler : SimpleChannelInboundHandler<IByteBuffer>
    {
        private readonly IByteBuffer _buffer = Unpooled.Buffer(256);
        private readonly EncryptionHandshake _handshake = new EncryptionHandshake();
        private readonly ILogManager _logManager;
        private readonly IEventExecutorGroup _group;
        private readonly ILogger _logger;
        private readonly HandshakeRole _role;

        private readonly IEncryptionHandshakeService _service;
        private readonly ISession _session;
        private PublicKey _remoteId;
        private readonly TaskCompletionSource<object> _initCompletionSource;
        private IChannel _channel;

        public NettyHandshakeHandler(
            IEncryptionHandshakeService service,
            ISession session,
            HandshakeRole role,
            PublicKey remoteId,
            ILogManager logManager,
            IEventExecutorGroup group)
        {
            _handshake.RemoteNodeId = remoteId;
            _role = role;
            _remoteId = remoteId;
            _logManager = logManager ?? throw new ArgumentNullException(nameof(NettyHandshakeHandler));
            _group = @group;
            _logger = logManager.GetClassLogger<NettyHandshakeHandler>();
            _service = service;
            _session = session;
            _initCompletionSource = new TaskCompletionSource<object>();
        }

        public override void ChannelActive(IChannelHandlerContext context)
        {
            _channel = context.Channel;

            if (_role == HandshakeRole.Initiator)
            {
                Packet auth = _service.Auth(_remoteId, _handshake);

                if (_logger.IsTrace) _logger.Trace($"Sending AUTH to {_remoteId} @ {context.Channel.RemoteAddress}");
                _buffer.WriteBytes(auth.Data);
                context.WriteAndFlushAsync(_buffer);
            }

            _session.RemoteHost = ((IPEndPoint) context.Channel.RemoteAddress).Address.ToString();
            _session.RemotePort = ((IPEndPoint) context.Channel.RemoteAddress).Port;

            CheckHandshakeInitTimeout().ContinueWith(x =>
            {
                if (x.IsFaulted && _logger.IsError)
                {
                    _logger.Error("Error during handshake timeout logic", x.Exception);
                }
            });
        }

        public override void ChannelInactive(IChannelHandlerContext context)
        {
            if (_logger.IsTrace) _logger.Trace("Channel Inactive");
            base.ChannelInactive(context);
        }

        public override Task DisconnectAsync(IChannelHandlerContext context)
        {
            if (_logger.IsTrace) _logger.Trace("Disconnected");
            return base.DisconnectAsync(context);
        }

        public override void ChannelUnregistered(IChannelHandlerContext context)
        {
            if (_logger.IsTrace) _logger.Trace("Channel Unregistered");
            base.ChannelUnregistered(context);
        }

        public override void ChannelRegistered(IChannelHandlerContext context)
        {
            if (_logger.IsTrace) _logger.Trace("Channel Registered");
            base.ChannelRegistered(context);
        }

        public override void ExceptionCaught(IChannelHandlerContext context, Exception exception)
        {
            string clientId = _session.Node?.ClientId ?? $"unknown {_session?.RemoteHost}";
            //In case of SocketException we log it as debug to avoid noise
            if (exception is SocketException)
            {
                if (_logger.IsTrace)
                {
                    _logger.Trace($"Handshake failure in communication with {clientId} (SocketException): {exception}");
                }
            }
            else
            {
                if (_logger.IsDebug)
                {
                    _logger.Debug($"Handshake failure in communication with {clientId}: {exception}");
                }
            }

            base.ExceptionCaught(context, exception);
        }

        protected override void ChannelRead0(IChannelHandlerContext context, IByteBuffer message)
        {
            if (_logger.IsTrace) _logger.Trace($"Channel read {nameof(NettyHandshakeHandler)} from {context.Channel.RemoteAddress}");
            if (message is IByteBuffer byteBuffer)
            {
                if (_role == HandshakeRole.Recipient)
                {
                    if (_logger.IsTrace) _logger.Trace($"AUTH received from {context.Channel.RemoteAddress}");
                    byte[] authData = new byte[byteBuffer.ReadableBytes];
                    byteBuffer.ReadBytes(authData);
                    Packet ack = _service.Ack(_handshake, new Packet(authData));

                    //_p2PSession.RemoteNodeId = _remoteId;
                    if (_logger.IsTrace) _logger.Trace($"Sending ACK to {_remoteId} @ {context.Channel.RemoteAddress}");
                    _buffer.WriteBytes(ack.Data);
                    context.WriteAndFlushAsync(_buffer);
                }
                else
                {
                    if (_logger.IsTrace) _logger.Trace($"Received ACK from {_remoteId} @ {context.Channel.RemoteAddress}");
                    byte[] ackData = new byte[byteBuffer.ReadableBytes];
                    byteBuffer.ReadBytes(ackData);
                    _service.Agree(_handshake, new Packet(ackData));
                }

                _remoteId = _handshake.RemoteNodeId;
                _initCompletionSource?.SetResult(message);
                _session.Handshake(_handshake.RemoteNodeId);

                FrameCipher frameCipher = new FrameCipher(_handshake.Secrets.AesSecret);
                FrameMacProcessor macProcessor = new FrameMacProcessor(_session.RemoteNodeId, _handshake.Secrets);

                if (_logger.IsTrace) _logger.Trace($"Registering {nameof(NettyFrameDecoder)} for {_remoteId} @ {context.Channel.RemoteAddress}");
                if (_logger.IsTrace) _logger.Trace($"Registering {nameof(NettyFrameEncoder)} for {_remoteId} @ {context.Channel.RemoteAddress}");
                if (_logger.IsTrace) _logger.Trace($"Registering {nameof(NettyFrameMerger)} for {_remoteId} @ {context.Channel.RemoteAddress}");
                if (_logger.IsTrace) _logger.Trace($"Registering {nameof(NettyPacketSplitter)} for {_remoteId} @ {context.Channel.RemoteAddress}");
                context.Channel.Pipeline.AddLast(
                    new NettyFrameDecoder(frameCipher, macProcessor, _logger),
                    new NettyFrameEncoder(frameCipher, macProcessor, _logger),
                    new NettyFrameMerger(_logger),
                    new NettyPacketSplitter());
                PacketSender packetSender = new PacketSender(_logManager);
                
                if (_logger.IsTrace) _logger.Trace($"Registering {nameof(PacketSender)} for {_session.RemoteNodeId} @ {context.Channel.RemoteAddress}");
                context.Channel.Pipeline.AddLast(packetSender);

                if (_logger.IsTrace) _logger.Trace($"Registering {nameof(NettyP2PHandler)} for {_remoteId} @ {context.Channel.RemoteAddress}");
                NettyP2PHandler handler = new NettyP2PHandler(_session, _logger);
                context.Channel.Pipeline.AddLast(_group, handler);

                handler.Init(packetSender, context);

                if (_logger.IsTrace) _logger.Trace($"Removing {nameof(NettyHandshakeHandler)}");
                context.Channel.Pipeline.Remove(this);
                if (_logger.IsTrace) _logger.Trace($"Removing {nameof(LengthFieldBasedFrameDecoder)}");
                context.Channel.Pipeline.Remove<LengthFieldBasedFrameDecoder>();
            }
            else
            {
                if (_logger.IsError) _logger.Error($"DIFFERENT TYPE OF DATA {message.GetType()}");
            }
        }

        public override void HandlerRemoved(IChannelHandlerContext context)
        {
            if (_logger.IsTrace) _logger.Trace($"Handshake with {_remoteId} @ {context.Channel.RemoteAddress} finished. Removing {nameof(NettyHandshakeHandler)} from the pipeline");
        }

        private async Task CheckHandshakeInitTimeout()
        {
            var receivedInitMsgTask = _initCompletionSource.Task;
            var firstTask = await Task.WhenAny(receivedInitMsgTask, Task.Delay(Timeouts.Handshake));

            if (firstTask != receivedInitMsgTask)
            {
                Metrics.HandshakeTimeouts++;
                if (_logger.IsTrace) _logger.Trace($"Disconnecting due to timeout for handshake: {_session.RemoteNodeId}@{_session.RemoteHost}:{_session.RemotePort}");
                //It will trigger channel.CloseCompletion which will trigger DisconnectAsync on the session
                await _channel.DisconnectAsync();
            }
        }
    }
}