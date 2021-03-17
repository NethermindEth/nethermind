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
using DotNetty.Buffers;
using DotNetty.Transport.Channels;
using DotNetty.Transport.Channels.Sockets;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Logging;
using Nethermind.Network.Discovery.Messages;

namespace Nethermind.Network.Discovery
{
    public class NettyDiscoveryHandler : SimpleChannelInboundHandler<DatagramPacket>, IMessageSender
    {
        private readonly ILogger _logger;
        private readonly IDiscoveryManager _discoveryManager;
        private readonly IDatagramChannel _channel;
        private readonly IMessageSerializationService _messageSerializationService;
        private readonly ITimestamper _timestamper;

        public NettyDiscoveryHandler(IDiscoveryManager discoveryManager, IDatagramChannel channel, IMessageSerializationService messageSerializationService, ITimestamper timestamper, ILogManager logManager)
        {
            _logger = logManager?.GetClassLogger<NettyDiscoveryHandler>() ?? throw new ArgumentNullException(nameof(logManager));
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

            context.DisconnectAsync().ContinueWith(x =>
            {
                if (x.IsFaulted && _logger.IsTrace)
                    _logger.Trace($"Error while disconnecting on context on {this} : {x.Exception}");
            });
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
                if (_logger.IsTrace) _logger.Trace($"Sending message: {discoveryMessage}");
                message = Serialize(discoveryMessage);
            }
            catch (Exception e)
            {
                _logger.Error($"Error during serialization of the message: {discoveryMessage}", e);
                return;
            }

            IAddressedEnvelope<IByteBuffer> packet = new DatagramPacket(Unpooled.CopiedBuffer(message), discoveryMessage.FarAddress);
            // _logger.Info($"The message {discoveryMessage} will be sent to {_channel.RemoteAddress}");
            await _channel.WriteAndFlushAsync(packet).ContinueWith(t =>
            {
                if (t.IsFaulted)
                {
                    if (_logger.IsTrace) _logger.Trace($"Error when sending a discovery message Msg: {discoveryMessage.ToString()} ,Exp: {t.Exception}");
                }
            });

            Interlocked.Add(ref Metrics.DiscoveryBytesSent, message.Length);
        }
        protected override void ChannelRead0(IChannelHandlerContext ctx, DatagramPacket packet)
        {
            IByteBuffer content = packet.Content;
            EndPoint address = packet.Sender;

            byte[] msg = new byte[content.ReadableBytes];
            content.ReadBytes(msg);
            
            Interlocked.Add(ref Metrics.DiscoveryBytesReceived, msg.Length);

            if (msg.Length < 98)
            {
                if (_logger.IsDebug) _logger.Debug($"Incorrect discovery message, length: {msg.Length}, sender: {address}");
                return;
            }

            byte typeRaw = msg[97];
            if (!Enum.IsDefined(typeof(MessageType), (int) typeRaw))
            {
                if (_logger.IsDebug) _logger.Debug($"Unsupported message type: {typeRaw}, sender: {address}, message {msg.ToHexString()}");
                return;
            }

            MessageType type = (MessageType) typeRaw;
            if (_logger.IsTrace) _logger.Trace($"Received message: {type}");

            DiscoveryMessage message;

            try
            {
                message = Deserialize(type, msg);
                message.FarAddress = (IPEndPoint) address;
            }
            catch (Exception e)
            {
                if (_logger.IsDebug) _logger.Debug($"Error during deserialization of the message, type: {type}, sender: {address}, msg: {msg.ToHexString()}, {e.Message}");
                return;
            }

            try
            {
                ReportMessageByType(message);

                if (!ValidateMessage(message, type, address, ctx, packet))
                    return;

                _discoveryManager.OnIncomingMessage(message);
            }
            catch (Exception e)
            {
                if (_logger.IsDebug) _logger.Error($"DEBUG/ERROR Error while processing message, type: {type}, sender: {address}, message: {message}", e);
            }
        }

        private DiscoveryMessage Deserialize(MessageType type, byte[] msg)
        {
            return type switch
            {
                MessageType.Ping => _messageSerializationService.Deserialize<PingMessage>(msg),
                MessageType.Pong => _messageSerializationService.Deserialize<PongMessage>(msg),
                MessageType.FindNode => _messageSerializationService.Deserialize<FindNodeMessage>(msg),
                MessageType.Neighbors => _messageSerializationService.Deserialize<NeighborsMessage>(msg),
                _ => throw new Exception($"Unsupported messageType: {type}")
            };
        }

        private byte[] Serialize(DiscoveryMessage message)
        {
            return message.MessageType switch
            {
                MessageType.Ping => _messageSerializationService.Serialize((PingMessage) message),
                MessageType.Pong => _messageSerializationService.Serialize((PongMessage) message),
                MessageType.FindNode => _messageSerializationService.Serialize((FindNodeMessage) message),
                MessageType.Neighbors => _messageSerializationService.Serialize((NeighborsMessage) message),
                _ => throw new Exception($"Unsupported messageType: {message.MessageType}")
            };
        }

        private bool ValidateMessage(DiscoveryMessage message, MessageType type, EndPoint address, IChannelHandlerContext ctx, DatagramPacket packet)
        {
            var timeToExpire = message.ExpirationTime - _timestamper.UnixTime.SecondsLong;
            if (timeToExpire < 0)
            {
                if (NetworkDiagTracer.IsEnabled) NetworkDiagTracer.ReportIncomingMessage(message.FarAddress, "HANDLER disc v4", $"{message.MessageType.ToString()} expired");
                if (_logger.IsDebug) _logger.Debug($"Received a discovery message that has expired {-timeToExpire} seconds ago, type: {type}, sender: {address}, message: {message}");
                return false;
            }

            if (!message.FarAddress.Equals((IPEndPoint) packet.Sender))
            {
                if (NetworkDiagTracer.IsEnabled) NetworkDiagTracer.ReportIncomingMessage(message.FarAddress, "HANDLER disc v4", $"{message.MessageType.ToString()} has incorrect far address");
                if (_logger.IsDebug) _logger.Debug($"Discovery fake IP detected - pretended {message.FarAddress} but was {ctx.Channel.RemoteAddress}, type: {type}, sender: {address}, message: {message}");
                return false;
            }

            if (message.FarPublicKey == null)
            {
                if (NetworkDiagTracer.IsEnabled) NetworkDiagTracer.ReportIncomingMessage(message.FarAddress, "HANDLER disc v4", $"{message.MessageType.ToString()} has null far public key");
                if (_logger.IsDebug) _logger.Debug($"Discovery message without a valid signature {message.FarAddress} but was {ctx.Channel.RemoteAddress}, type: {type}, sender: {address}, message: {message}");
                return false;
            }

            return true;
        }
        
        private void ReportMessageByType(DiscoveryMessage message)
        {
                if (message is PingMessage pingMessage)
                {
                    if(NetworkDiagTracer.IsEnabled) NetworkDiagTracer.ReportIncomingMessage(pingMessage.FarAddress, "HANDLER disc v4", $"PING {pingMessage.SourceAddress.Address} -> {pingMessage.DestinationAddress.Address}");    
                }
                else
                {
                    if(NetworkDiagTracer.IsEnabled) NetworkDiagTracer.ReportIncomingMessage(message.FarAddress, "HANDLER disc v4", message.MessageType.ToString());    
                }
        }
        
        public event EventHandler OnChannelActivated;
    }
}
