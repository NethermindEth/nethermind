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
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using DotNetty.Buffers;
using DotNetty.Codecs;
using DotNetty.Common.Concurrency;
using DotNetty.Handlers.Timeout;
using DotNetty.Transport.Channels;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.Network.P2P;
using Nethermind.Network.Rlpx.Handshake;

namespace Nethermind.Network.Rlpx
{
    public class NettyHandshakeHandler : SimpleChannelInboundHandler<IByteBuffer>
    {
        private readonly EncryptionHandshake _handshake = new EncryptionHandshake();
        private readonly IMessageSerializationService _serializationService;
        private readonly ILogManager _logManager;
        private readonly IEventExecutorGroup _group;
        private readonly ILogger _logger;
        private readonly HandshakeRole _role;

        private readonly IHandshakeService _service;
        private readonly ISession _session;
        private PublicKey RemoteId => _session.RemoteNodeId;
        private readonly TaskCompletionSource<object> _initCompletionSource;
        private IChannel _channel;

        public NettyHandshakeHandler(
            IMessageSerializationService serializationService,
            IHandshakeService handshakeService,
            ISession session,
            HandshakeRole role,
            ILogManager logManager,
            IEventExecutorGroup group)
        {
            _serializationService = serializationService ?? throw new ArgumentNullException(nameof(serializationService));
            _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
            _logger = logManager.GetClassLogger<NettyHandshakeHandler>();
            _role = role;
            _group = group;
            _service = handshakeService ?? throw new ArgumentNullException(nameof(handshakeService));
            _session = session ?? throw new ArgumentNullException(nameof(session));
            _initCompletionSource = new TaskCompletionSource<object>();
        }

        public override void ChannelActive(IChannelHandlerContext context)
        {
            _channel = context.Channel;

            if (_role == HandshakeRole.Initiator)
            {
                Packet auth = _service.Auth(RemoteId, _handshake);

                if (_logger.IsTrace) _logger.Trace($"Sending AUTH to {RemoteId} @ {context.Channel.RemoteAddress}");
                IByteBuffer buffer = PooledByteBufferAllocator.Default.Buffer();
                buffer.WriteBytes(auth.Data);
                context.WriteAndFlushAsync(buffer);
                Interlocked.Add(ref Metrics.P2PBytesSent, auth.Data.Length);
            }
            else
            {
                _session.RemoteHost = ((IPEndPoint) context.Channel.RemoteAddress).Address.ToString();
                _session.RemotePort = ((IPEndPoint) context.Channel.RemoteAddress).Port;   
            }

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
            string clientId = _session?.Node?.ToString("c") ?? $"unknown {_session?.RemoteHost}";
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

        protected override void ChannelRead0(IChannelHandlerContext context, IByteBuffer input)
        {
            if (_logger.IsTrace) _logger.Trace($"Channel read {nameof(NettyHandshakeHandler)} from {context.Channel.RemoteAddress}");

            if (_role == HandshakeRole.Recipient)
            {
                if (_logger.IsTrace) _logger.Trace($"AUTH received from {context.Channel.RemoteAddress}");
                byte[] authData = new byte[input.ReadableBytes];
                input.ReadBytes(authData);
                Interlocked.Add(ref Metrics.P2PBytesReceived, authData.Length);
                Packet ack = _service.Ack(_handshake, new Packet(authData));

                //_p2PSession.RemoteNodeId = _remoteId;
                if (_logger.IsTrace) _logger.Trace($"Sending ACK to {RemoteId} @ {context.Channel.RemoteAddress}");
                IByteBuffer buffer = PooledByteBufferAllocator.Default.Buffer();
                buffer.WriteBytes(ack.Data);
                context.WriteAndFlushAsync(buffer);
                Interlocked.Add(ref Metrics.P2PBytesSent, ack.Data.Length);
            }
            else
            {
                if (_logger.IsTrace) _logger.Trace($"Received ACK from {RemoteId} @ {context.Channel.RemoteAddress}");
                byte[] ackData = new byte[input.ReadableBytes];
                Interlocked.Add(ref Metrics.P2PBytesReceived, ackData.Length);
                input.ReadBytes(ackData);
                _service.Agree(_handshake, new Packet(ackData));
            }

            _initCompletionSource?.SetResult(input);
            _session.Handshake(_handshake.RemoteNodeId);

            FrameCipher frameCipher = new FrameCipher(_handshake.Secrets.AesSecret);
            FrameMacProcessor macProcessor = new FrameMacProcessor(_session.RemoteNodeId, _handshake.Secrets);

            if (_role == HandshakeRole.Recipient)
            {
                if (_logger.IsTrace) _logger.Trace($"Registering {nameof(LengthFieldBasedFrameDecoder)}  for {RemoteId} @ {context.Channel.RemoteAddress}");
                context.Channel.Pipeline.AddLast("enc-handshake-dec", new LengthFieldBasedFrameDecoder(ByteOrder.BigEndian, ushort.MaxValue, 0, 2, 0, 0, true));
            }
            if (_logger.IsTrace) _logger.Trace($"Registering {nameof(ReadTimeoutHandler)} for {RemoteId} @ {context.Channel.RemoteAddress}");
            context.Channel.Pipeline.AddLast(new ReadTimeoutHandler(TimeSpan.FromSeconds(30))); // read timeout instead of session monitoring
            if (_logger.IsTrace) _logger.Trace($"Registering {nameof(ZeroFrameDecoder)} for {RemoteId} @ {context.Channel.RemoteAddress}");
            context.Channel.Pipeline.AddLast(new ZeroFrameDecoder(frameCipher, macProcessor, _logManager));
            if (_logger.IsTrace) _logger.Trace($"Registering {nameof(ZeroFrameEncoder)} for {RemoteId} @ {context.Channel.RemoteAddress}");
            context.Channel.Pipeline.AddLast(new ZeroFrameEncoder(frameCipher, macProcessor, _logManager));
            if (_logger.IsTrace) _logger.Trace($"Registering {nameof(ZeroFrameMerger)} for {RemoteId} @ {context.Channel.RemoteAddress}");
            context.Channel.Pipeline.AddLast(new ZeroFrameMerger(_logManager));
            if (_logger.IsTrace) _logger.Trace($"Registering {nameof(ZeroPacketSplitter)} for {RemoteId} @ {context.Channel.RemoteAddress}");
            context.Channel.Pipeline.AddLast(new ZeroPacketSplitter(_logManager));

            PacketSender packetSender = new PacketSender(_serializationService, _logManager);
            if (_logger.IsTrace) _logger.Trace($"Registering {nameof(PacketSender)} for {_session.RemoteNodeId} @ {context.Channel.RemoteAddress}");
            context.Channel.Pipeline.AddLast(packetSender);

            if (_logger.IsTrace) _logger.Trace($"Registering {nameof(ZeroNettyP2PHandler)} for {RemoteId} @ {context.Channel.RemoteAddress}");
            ZeroNettyP2PHandler handler = new ZeroNettyP2PHandler(_session, _logManager);
            context.Channel.Pipeline.AddLast(_group, handler);

            handler.Init(packetSender, context);

            if (_logger.IsTrace) _logger.Trace($"Removing {nameof(NettyHandshakeHandler)}");
            context.Channel.Pipeline.Remove(this);
            if (_logger.IsTrace) _logger.Trace($"Removing {nameof(LengthFieldBasedFrameDecoder)}");
            context.Channel.Pipeline.Remove<LengthFieldBasedFrameDecoder>();
        }

        public override void HandlerRemoved(IChannelHandlerContext context)
        {
            if (_logger.IsTrace) _logger.Trace($"Handshake with {RemoteId} @ {context.Channel.RemoteAddress} finished. Removing {nameof(NettyHandshakeHandler)} from the pipeline");
        }

        private async Task CheckHandshakeInitTimeout()
        {
            Task<object> receivedInitMsgTask = _initCompletionSource.Task;
            CancellationTokenSource delayCancellation = new CancellationTokenSource();
            Task firstTask = await Task.WhenAny(receivedInitMsgTask, Task.Delay(Timeouts.Handshake, delayCancellation.Token));

            if (firstTask != receivedInitMsgTask)
            {
                Metrics.HandshakeTimeouts++;
                if (_logger.IsTrace) _logger.Trace($"Disconnecting due to timeout for handshake: {_session.RemoteNodeId}@{_session.RemoteHost}:{_session.RemotePort}");
                //It will trigger channel.CloseCompletion which will trigger DisconnectAsync on the session
                await _channel.DisconnectAsync();
            }
            else
            {
                delayCancellation.Cancel();    
            }
        }
    }
}
