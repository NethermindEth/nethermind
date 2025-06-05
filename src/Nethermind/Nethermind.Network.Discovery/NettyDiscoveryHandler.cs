// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net;
using System.Net.Sockets;
using DotNetty.Buffers;
using DotNetty.Transport.Channels;
using DotNetty.Transport.Channels.Sockets;
using FastEnumUtility;
using Nethermind.Core;
using Nethermind.Core.Buffers;
using Nethermind.Core.Collections;
using Nethermind.Core.Extensions;
using Nethermind.Logging;
using Nethermind.Network.Discovery.Messages;
using ILogger = Nethermind.Logging.ILogger;

namespace Nethermind.Network.Discovery;

public class NettyDiscoveryHandler : NettyDiscoveryBaseHandler, IMsgSender
{
    private readonly ILogger _logger;
    private readonly IDiscoveryMsgListener _discoveryMsgListener;
    private readonly IChannel _channel;
    private readonly IMessageSerializationService _msgSerializationService;
    private readonly ITimestamper _timestamper;

    public NettyDiscoveryHandler(
        IDiscoveryMsgListener? discoveryManager,
        IChannel? channel,
        IMessageSerializationService? msgSerializationService,
        ITimestamper? timestamper,
        ILogManager? logManager) : base(logManager)
    {
        _logger = logManager?.GetClassLogger<NettyDiscoveryHandler>() ?? throw new ArgumentNullException(nameof(logManager));
        _discoveryMsgListener = discoveryManager ?? throw new ArgumentNullException(nameof(discoveryManager));
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

    public async Task SendMsg(DiscoveryMsg discoveryMsg)
    {
        IByteBuffer msgBuffer;
        try
        {
            if (_logger.IsTrace) _logger.Trace($"Sending message: {discoveryMsg}");
            msgBuffer = Serialize(discoveryMsg, PooledByteBufferAllocator.Default);
        }
        catch (Exception e)
        {
            _logger.Error($"Error during serialization of the message: {discoveryMsg}", e);
            return;
        }

        int size = msgBuffer.ReadableBytes;
        if (size > MaxPacketSize)
        {
            if (_logger.IsWarn) _logger.Warn($"Attempting to send message larger than 1280 bytes. This is out of spec and may not work for all clients. Msg: ${discoveryMsg}");
        }

        if (discoveryMsg is PingMsg pingMessage)
        {
            if (NetworkDiagTracer.IsEnabled) NetworkDiagTracer.ReportOutgoingMessage(pingMessage.FarAddress, "disc v4", $"Ping {pingMessage.SourceAddress?.Address} -> {pingMessage.DestinationAddress?.Address}", size);
        }
        else
        {
            if (NetworkDiagTracer.IsEnabled) NetworkDiagTracer.ReportOutgoingMessage(discoveryMsg.FarAddress, "disc v4", discoveryMsg.MsgType.ToString(), size);
        }

        IAddressedEnvelope<IByteBuffer> packet = new DatagramPacket(msgBuffer, discoveryMsg.FarAddress);
        try
        {
            await _channel.WriteAndFlushAsync(packet);
        }
        catch (Exception e)
        {
            if (_logger.IsTrace) _logger.Trace($"Error when sending a discovery message Msg: {discoveryMsg} ,Exp: {e}");
        }

        Interlocked.Add(ref Metrics.DiscoveryBytesSent, size);
        Metrics.DiscoveryMessagesSent.Increment(discoveryMsg.MsgType);
    }

    private bool TryParseMessage(DatagramPacket packet, out DiscoveryMsg? msg)
    {
        msg = null;

        IByteBuffer content = packet.Content;
        EndPoint address = packet.Sender;

        int size = content.ReadableBytes;
        using var handle = ArrayPoolDisposableReturn.Rent(size, out byte[] msgBytes);

        content.ReadBytes(msgBytes, 0, size);

        Interlocked.Add(ref Metrics.DiscoveryBytesReceived, size);

        if (msgBytes.Length < 98)
        {
            if (_logger.IsDebug) _logger.Debug($"Incorrect discovery message, length: {size}, sender: {address}");
            return false;
        }

        byte typeRaw = msgBytes[97];
        if (!FastEnum.IsDefined((MsgType)typeRaw))
        {
            if (_logger.IsDebug) _logger.Debug($"Unsupported message type: {typeRaw}, sender: {address}, message {msgBytes.AsSpan(0, size).ToHexString()}");
            return false;
        }

        MsgType type = (MsgType)typeRaw;
        if (_logger.IsTrace) _logger.Trace($"Received message: {type}");

        try
        {
            msg = Deserialize(type, new ArraySegment<byte>(msgBytes, 0, size));
            msg.FarAddress = (IPEndPoint)address;
        }
        catch (Exception e)
        {
            if (_logger.IsDebug) _logger.Debug($"Error during deserialization of the message, type: {type}, sender: {address}, msg: {msgBytes.AsSpan(0, size).ToHexString()}, {e.Message}");
            return false;
        }

        return true;
    }

    protected override void ChannelRead0(IChannelHandlerContext ctx, DatagramPacket packet)
    {
        if (!TryParseMessage(packet, out DiscoveryMsg? msg) || msg == null)
        {
            packet.Content.ResetReaderIndex();
            ctx.FireChannelRead(packet.Retain());
            return;
        }

        MsgType type = msg.MsgType;
        EndPoint address = packet.Sender;
        int size = packet.Content.ReadableBytes;

        try
        {
            ReportMsgByType(msg, size);

            if (!ValidateMsg(msg, type, address, ctx, packet, size))
                return;

            // Explicitly run it on the default scheduler to prevent something down the line hanging netty task scheduler.
            Task.Factory.StartNew(
                () => _discoveryMsgListener.OnIncomingMsg(msg),
                CancellationToken.None,
                TaskCreationOptions.RunContinuationsAsynchronously,
                TaskScheduler.Default
            );
        }
        catch (Exception e)
        {
            if (_logger.IsDebug) _logger.Error($"DEBUG/ERROR Error while processing message, type: {type}, sender: {address}, message: {msg}", e);
        }
    }

    private DiscoveryMsg Deserialize(MsgType type, ArraySegment<byte> msg)
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

    private IByteBuffer Serialize(DiscoveryMsg msg, AbstractByteBufferAllocator? allocator)
    {
        return msg.MsgType switch
        {
            MsgType.Ping => _msgSerializationService.ZeroSerialize((PingMsg)msg, allocator),
            MsgType.Pong => _msgSerializationService.ZeroSerialize((PongMsg)msg, allocator),
            MsgType.FindNode => _msgSerializationService.ZeroSerialize((FindNodeMsg)msg, allocator),
            MsgType.Neighbors => _msgSerializationService.ZeroSerialize((NeighborsMsg)msg, allocator),
            MsgType.EnrRequest => _msgSerializationService.ZeroSerialize((EnrRequestMsg)msg, allocator),
            MsgType.EnrResponse => _msgSerializationService.ZeroSerialize((EnrResponseMsg)msg, allocator),
            _ => throw new Exception($"Unsupported messageType: {msg.MsgType}")
        };
    }

    private bool ValidateMsg(DiscoveryMsg msg, MsgType type, EndPoint address, IChannelHandlerContext ctx, DatagramPacket packet, int size)
    {
        long timeToExpire = msg.ExpirationTime - _timestamper.UnixTime.SecondsLong;
        if (timeToExpire < 0)
        {
            if (NetworkDiagTracer.IsEnabled) NetworkDiagTracer.ReportIncomingMessage(msg.FarAddress, "disc v4", $"{msg.MsgType} expired", size);
            if (_logger.IsDebug) _logger.Debug($"Received a discovery message that has expired {-timeToExpire} seconds ago, type: {type}, sender: {address}, message: {msg}");
            return false;
        }

        if (msg.FarAddress is null)
        {
            if (NetworkDiagTracer.IsEnabled) NetworkDiagTracer.ReportIncomingMessage(msg.FarAddress, "disc v4", $"{msg.MsgType} has null far address", size);
            if (_logger.IsDebug) _logger.Debug($"Discovery message without a valid far address {msg.FarAddress}, type: {type}, sender: {address}, message: {msg}");
            return false;
        }

        if (!msg.FarAddress.Equals((IPEndPoint)packet.Sender))
        {
            if (NetworkDiagTracer.IsEnabled) NetworkDiagTracer.ReportIncomingMessage(msg.FarAddress, "disc v4", $"{msg.MsgType} has incorrect far address", size);
            if (_logger.IsDebug) _logger.Debug($"Discovery fake IP detected - pretended {msg.FarAddress} but was {ctx.Channel.RemoteAddress}, type: {type}, sender: {address}, message: {msg}");
            return false;
        }

        if (msg.FarPublicKey is null)
        {
            if (NetworkDiagTracer.IsEnabled) NetworkDiagTracer.ReportIncomingMessage(msg.FarAddress, "disc v4", $"{msg.MsgType} has null far public key", size);
            if (_logger.IsDebug) _logger.Debug($"Discovery message without a valid signature {msg.FarAddress} but was {ctx.Channel.RemoteAddress}, type: {type}, sender: {address}, message: {msg}");
            return false;
        }

        return true;
    }

    private static void ReportMsgByType(DiscoveryMsg msg, int size)
    {
        if (msg is PingMsg pingMsg)
        {
            if (NetworkDiagTracer.IsEnabled) NetworkDiagTracer.ReportIncomingMessage(pingMsg.FarAddress, "disc v4", $"PING {pingMsg.SourceAddress.Address} -> {pingMsg.DestinationAddress?.Address}", size);
        }
        else
        {
            if (NetworkDiagTracer.IsEnabled) NetworkDiagTracer.ReportIncomingMessage(msg.FarAddress, "disc v4", msg.MsgType.ToString(), size);
        }
        Metrics.DiscoveryMessagesReceived.Increment(msg.MsgType);
    }

    public event EventHandler? OnChannelActivated;
}
