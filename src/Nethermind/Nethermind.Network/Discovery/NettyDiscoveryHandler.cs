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
using DotNetty.Buffers;
using DotNetty.Transport.Channels;
using DotNetty.Transport.Channels.Sockets;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Logging;
using Nethermind.Network.Discovery.Messages;

namespace Nethermind.Network.Discovery
{
    public class NettyDiscoveryHandler : SimpleChannelInboundHandler<DatagramPacket>, IMessageSender, INettyChannel
    {
        private readonly ILogger _logger;
        private readonly IDiscoveryManager _discoveryManager;
        private readonly IDatagramChannel _channel;
        private readonly IMessageSerializationService _messageSerializationService;
        private readonly ITimestamper _timestamper;

        public NettyDiscoveryHandler(IDiscoveryManager discoveryManager, IDatagramChannel channel, IMessageSerializationService messageSerializationService, ITimestamper timestamper, ILogManager logManager)
        {
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            _discoveryManager = discoveryManager ?? throw new ArgumentNullException(nameof(discoveryManager));
            _channel = channel ?? throw new ArgumentNullException(nameof(channel));
            _messageSerializationService = messageSerializationService ?? throw new ArgumentNullException(nameof(messageSerializationService));
            _timestamper = timestamper ?? throw new ArgumentNullException(nameof(timestamper));
        }

        public override void ChannelActive(IChannelHandlerContext context)
        {
            OnChannelActivated?.Invoke(this, EventArgs.Empty);
        }

        public override void ExceptionCaught(IChannelHandlerContext context, Exception exception)
        {
            //In case of SocketException we log it as debug to avoid noise
            if (exception is SocketException)
            {
                if (_logger.IsTrace) _logger.Trace($"Exception when processing discovery messages (SocketException): {exception}");
            }
            else
            {
                if (_logger.IsError) _logger.Error("Exception when processing discovery messages", exception);
            }

            context.DisconnectAsync();
        }

        public override void ChannelReadComplete(IChannelHandlerContext context)
        {
            context.Flush();
        }

        public async void SendMessage(DiscoveryMessage discoveryMessage)
        {
            byte[] message;

            try
            {
                if(_logger.IsTrace) _logger.Trace($"Sending message: {discoveryMessage}");
                message = Serialize(discoveryMessage);
            }
            catch (Exception e)
            {
                _logger.Error($"Error during serialization of the message: {discoveryMessage}", e);
                return;
            }
            
            IAddressedEnvelope<IByteBuffer> packet = new DatagramPacket(Unpooled.CopiedBuffer(message), discoveryMessage.FarAddress);
            await _channel.WriteAndFlushAsync(packet).ContinueWith(t =>
            {
                if (t.IsFaulted)
                {
                    if(_logger.IsTrace) _logger.Trace($"Error when sending a discovery message Msg: {discoveryMessage.ToString()} ,Exp: {t.Exception}");
                }
            });
        }

        protected override void ChannelRead0(IChannelHandlerContext ctx, DatagramPacket packet)
        {
            var content = packet.Content;
            var address = packet.Sender;

            byte[] msg = new byte[content.ReadableBytes];
            content.ReadBytes(msg);

            if (msg.Length < 98)
            {
                if(_logger.IsDebug) _logger.Debug($"Incorrect discovery message, length: {msg.Length}, sender: {address}");
                ctx.DisconnectAsync();
                return;
            }
            
            var typeRaw = msg[97];
            if (!Enum.IsDefined(typeof(MessageType), (int)typeRaw))
            {
                if(_logger.IsDebug) _logger.Debug($"Unsupported message type: {typeRaw}, sender: {address}, message {msg.ToHexString()}");
                ctx.DisconnectAsync();
                return;
            }
            
            var type = (MessageType)typeRaw;
            if(_logger.IsTrace) _logger.Trace($"Received message: {type}");

            DiscoveryMessage message;

            try
            {
                message = Deserialize(type, msg);
                message.FarAddress = (IPEndPoint)address;
            }
            catch (Exception e)
            {
                if(_logger.IsDebug) _logger.Debug($"Error during deserialization of the message, type: {type}, sender: {address}, msg: {msg.ToHexString()}, {e.Message}");
                ctx.DisconnectAsync();
                return;
            }

            try
            {
                if ((ulong)message.ExpirationTime < _timestamper.EpochSeconds)
                {
                    if(_logger.IsDebug) _logger.Debug($"Received a discovery message that has expired, type: {type}, sender: {address}, message: {message}");
                    ctx.DisconnectAsync();
                    return;
                }

                if (!message.FarAddress.Equals((IPEndPoint)packet.Sender))
                {
                    if(_logger.IsDebug) _logger.Debug($"Discovery fake IP detected - pretended {message.FarAddress} but was {ctx.Channel.RemoteAddress}, type: {type}, sender: {address}, message: {message}");
                    ctx.DisconnectAsync();
                    return;
                }
                
                if (message.FarPublicKey == null)
                {
                    if(_logger.IsDebug) _logger.Debug($"Discovery message without a valid signature {message.FarAddress} but was {ctx.Channel.RemoteAddress}, type: {type}, sender: {address}, message: {message}");
                    ctx.DisconnectAsync();
                    return;
                }

                _discoveryManager.OnIncomingMessage(message);
            }
            catch (Exception e)
            {
                if(_logger.IsDebug) _logger.Error($"DEBUG/ERROR Error while processing message, type: {type}, sender: {address}, message: {message}", e);
                ctx.DisconnectAsync();
            }
        }

        private DiscoveryMessage Deserialize(MessageType type, byte[] msg)
        {
            switch (type)
            {
                case MessageType.Ping:
                    return _messageSerializationService.Deserialize<PingMessage>(msg);
                case MessageType.Pong:
                    return _messageSerializationService.Deserialize<PongMessage>(msg);
                case MessageType.FindNode:
                    return _messageSerializationService.Deserialize<FindNodeMessage>(msg);
                case MessageType.Neighbors:
                    return _messageSerializationService.Deserialize<NeighborsMessage>(msg);
                default:
                    throw new Exception($"Unsupported messageType: {type}");
            }
        }

        private byte[] Serialize(DiscoveryMessage message)
        {
            switch (message.MessageType)
            {
                case MessageType.Ping:
                    return _messageSerializationService.Serialize((PingMessage)message);
                case MessageType.Pong:
                    return _messageSerializationService.Serialize((PongMessage)message);
                case MessageType.FindNode:
                    return _messageSerializationService.Serialize((FindNodeMessage)message);
                case MessageType.Neighbors:
                    return _messageSerializationService.Serialize((NeighborsMessage)message);
                default:
                    throw new Exception($"Unsupported messageType: {message.MessageType}");
            }
        }
        
        public event EventHandler OnChannelActivated;
    }
}