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

using System.Net;
using System.Net.Sockets;
using DotNetty.Buffers;
using DotNetty.Transport.Channels;
using DotNetty.Transport.Channels.Sockets;
using FastEnumUtility;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Logging;
using Nethermind.Network.Discovery.Messages;

namespace Nethermind.Network.Discovery;

public class NettyDiscoveryHandler : SimpleChannelInboundHandler<DatagramPacket>, IMsgSender
{
    private readonly ILogger _logger;
    private readonly IDiscoveryManager _discoveryManager;
    private readonly IDatagramChannel _channel;
    private readonly IMessageSerializationService _msgSerializationService;
    private readonly ITimestamper _timestamper;

    public NettyDiscoveryHandler(
        IDiscoveryManager? discoveryManager,
        IDatagramChannel? channel,
        IMessageSerializationService? msgSerializationService,
        ITimestamper? timestamper,
        ILogManager? logManager)
    {
        _logger = logManager?.GetClassLogger<NettyDiscoveryHandler>() ?? throw new ArgumentNullException(nameof(logManager));
        _discoveryManager = discoveryManager ?? throw new ArgumentNullException(nameof(discoveryManager));
        _channel = channel ?? throw new ArgumentNullException(nameof(channel));
        _msgSerializationService = msgSerializationService ?? throw new ArgumentNullException(nameof(msgSerializationService));
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

    public async void SendMsg(DiscoveryMsg discoveryMsg)
    {
        IByteBuffer msgBuffer = Unpooled.Buffer(64);

        try
        {
            if (_logger.IsTrace) _logger.Trace($"Sending message: {discoveryMsg}");
            Serialize(discoveryMsg, msgBuffer);
        }
        catch (Exception e)
        {
            _logger.Error($"Error during serialization of the message: {discoveryMsg}", e);
            return;
        }

        if (msgBuffer.ReadableBytes > 1280)
        {
            if (_logger.IsWarn) _logger.Warn($"Attempting to send message larger than 1280 bytes. This is out of spec and may not work for all client. Msg: ${discoveryMsg}");
        }

        IAddressedEnvelope<IByteBuffer> packet = new DatagramPacket(msgBuffer, discoveryMsg.FarAddress);

        await _channel.WriteAndFlushAsync(packet).ContinueWith(t =>
        {
            if (t.IsFaulted)
            {
                if (_logger.IsTrace) _logger.Trace($"Error when sending a discovery message Msg: {discoveryMsg.ToString()} ,Exp: {t.Exception}");
            }
        });

        Interlocked.Add(ref Metrics.DiscoveryBytesSent, msgBuffer.ReadableBytes);
    }
    protected override void ChannelRead0(IChannelHandlerContext ctx, DatagramPacket packet)
    {
        IByteBuffer content = packet.Content;
        EndPoint address = packet.Sender;

        byte[] msgBytes = new byte[content.ReadableBytes];
        content.ReadBytes(msgBytes);

        Interlocked.Add(ref Metrics.DiscoveryBytesReceived, msgBytes.Length);

        if (msgBytes.Length < 98)
        {
            if (_logger.IsDebug) _logger.Debug($"Incorrect discovery message, length: {msgBytes.Length}, sender: {address}");
            return;
        }

        byte typeRaw = msgBytes[97];
        if (!FastEnum.IsDefined<MsgType>((int) typeRaw))
        {
            if (_logger.IsDebug) _logger.Debug($"Unsupported message type: {typeRaw}, sender: {address}, message {msgBytes.ToHexString()}");
            return;
        }

        MsgType type = (MsgType) typeRaw;
        if (_logger.IsTrace) _logger.Trace($"Received message: {type}");

        DiscoveryMsg msg;

        try
        {
            msg = Deserialize(type, msgBytes);
            msg.FarAddress = (IPEndPoint) address;
        }
        catch (Exception e)
        {
            if (_logger.IsDebug) _logger.Debug($"Error during deserialization of the message, type: {type}, sender: {address}, msg: {msgBytes.ToHexString()}, {e.Message}");
            return;
        }

        try
        {
            ReportMsgByType(msg);

            if (!ValidateMsg(msg, type, address, ctx, packet))
                return;

            _discoveryManager.OnIncomingMsg(msg);
        }
        catch (Exception e)
        {
            if (_logger.IsDebug) _logger.Error($"DEBUG/ERROR Error while processing message, type: {type}, sender: {address}, message: {msg}", e);
        }
    }

    private DiscoveryMsg Deserialize(MsgType type, byte[] msg)
    {
        return type switch
        {
            MsgType.Ping => _msgSerializationService.Deserialize<PingMsg>(msg),
            MsgType.Pong => _msgSerializationService.Deserialize<PongMsg>(msg),
            MsgType.FindNode => _msgSerializationService.Deserialize<FindNodeMsg>(msg),
            MsgType.Neighbors => _msgSerializationService.Deserialize<NeighborsMsg>(msg),
            MsgType.EnrRequest => _msgSerializationService.Deserialize<EnrRequestMsg>(msg),
            MsgType.EnrResponse => _msgSerializationService.Deserialize<EnrResponseMsg>(msg),
            _ => throw new Exception($"Unsupported messageType: {type}")
        };
    }

    private byte[] Serialize(DiscoveryMsg msg)
    {
        return msg.MsgType switch
        {
            MsgType.Ping => _msgSerializationService.ZeroSerialize((PingMsg) msg).ReadAllBytes(),
            MsgType.Pong => _msgSerializationService.ZeroSerialize((PongMsg) msg).ReadAllBytes(),
            MsgType.FindNode => _msgSerializationService.ZeroSerialize((FindNodeMsg) msg).ReadAllBytes(),
            MsgType.Neighbors => _msgSerializationService.ZeroSerialize((NeighborsMsg) msg).ReadAllBytes(),
            MsgType.EnrRequest => _msgSerializationService.ZeroSerialize((EnrRequestMsg) msg).ReadAllBytes(),
            MsgType.EnrResponse => _msgSerializationService.ZeroSerialize((EnrResponseMsg) msg).ReadAllBytes(),
            _ => throw new Exception($"Unsupported messageType: {msg.MsgType}")
        };
    }

    private void Serialize(DiscoveryMsg msg, IByteBuffer msgBuffer)
    {
        switch (msg.MsgType)
        {
            case MsgType.Ping:
                _msgSerializationService.ZeroSerialize((PingMsg)msg, msgBuffer);
                break;
            case MsgType.Pong:
                _msgSerializationService.ZeroSerialize((PongMsg)msg, msgBuffer);
                break;
            case MsgType.FindNode :
                _msgSerializationService.ZeroSerialize((FindNodeMsg)msg, msgBuffer);
                break;
            case MsgType.Neighbors :
                _msgSerializationService.ZeroSerialize((NeighborsMsg)msg, msgBuffer);
                break;
            case MsgType.EnrRequest :
                _msgSerializationService.ZeroSerialize((EnrRequestMsg)msg, msgBuffer);
                break;
            case MsgType.EnrResponse :
                _msgSerializationService.ZeroSerialize((EnrResponseMsg)msg, msgBuffer);
                break;
            default:
                throw new Exception($"Unsupported messageType: {msg.MsgType}");
        }
    }

    private bool ValidateMsg(DiscoveryMsg msg, MsgType type, EndPoint address, IChannelHandlerContext ctx, DatagramPacket packet)
    {
        long timeToExpire = msg.ExpirationTime - _timestamper.UnixTime.SecondsLong;
        if (timeToExpire < 0)
        {
            if (NetworkDiagTracer.IsEnabled) NetworkDiagTracer.ReportIncomingMessage(msg.FarAddress, "HANDLER disc v4", $"{msg.MsgType.ToString()} expired");
            if (_logger.IsDebug) _logger.Debug($"Received a discovery message that has expired {-timeToExpire} seconds ago, type: {type}, sender: {address}, message: {msg}");
            return false;
        }

        if (msg.FarAddress == null)
        {
            if (NetworkDiagTracer.IsEnabled) NetworkDiagTracer.ReportIncomingMessage(msg.FarAddress, "HANDLER disc v4", $"{msg.MsgType.ToString()} has null far address");
            if (_logger.IsDebug) _logger.Debug($"Discovery message without a valid far address {msg.FarAddress}, type: {type}, sender: {address}, message: {msg}");
            return false;
        }

        if (!msg.FarAddress.Equals((IPEndPoint) packet.Sender))
        {
            if (NetworkDiagTracer.IsEnabled) NetworkDiagTracer.ReportIncomingMessage(msg.FarAddress, "HANDLER disc v4", $"{msg.MsgType.ToString()} has incorrect far address");
            if (_logger.IsDebug) _logger.Debug($"Discovery fake IP detected - pretended {msg.FarAddress} but was {ctx.Channel.RemoteAddress}, type: {type}, sender: {address}, message: {msg}");
            return false;
        }

        if (msg.FarPublicKey == null)
        {
            if (NetworkDiagTracer.IsEnabled) NetworkDiagTracer.ReportIncomingMessage(msg.FarAddress, "HANDLER disc v4", $"{msg.MsgType.ToString()} has null far public key");
            if (_logger.IsDebug) _logger.Debug($"Discovery message without a valid signature {msg.FarAddress} but was {ctx.Channel.RemoteAddress}, type: {type}, sender: {address}, message: {msg}");
            return false;
        }

        return true;
    }

    private static void ReportMsgByType(DiscoveryMsg msg)
    {
        if (msg is PingMsg pingMsg)
        {
            if(NetworkDiagTracer.IsEnabled) NetworkDiagTracer.ReportIncomingMessage(pingMsg.FarAddress, "HANDLER disc v4", $"PING {pingMsg.SourceAddress.Address} -> {pingMsg.DestinationAddress?.Address}");
        }
        else
        {
            if(NetworkDiagTracer.IsEnabled) NetworkDiagTracer.ReportIncomingMessage(msg.FarAddress, "HANDLER disc v4", msg.MsgType.ToString());
        }
    }

    public event EventHandler? OnChannelActivated;
}
