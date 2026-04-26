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

public class NettyDiscoveryHandler(
    IDiscoveryMsgListener? discoveryManager,
    IChannel? channel,
    IMessageSerializationService? msgSerializationService,
    ITimestamper? timestamper,
    ILogManager? logManager,
    NodeFilter? inboundMessageFilter = null) : NettyDiscoveryBaseHandler(logManager), IMsgSender
{
    private static readonly TimeSpan MaxFutureExpirationOffset = TimeSpan.FromHours(1);
    private static readonly TimeSpan DefaultInboundMessageWindow = TimeSpan.FromMilliseconds(100);
    private const int DefaultInboundMessageBurstPerIp = 4;
    private const int DefaultInboundMessageFilterSize = 8_192;
    private readonly ILogger _logger = logManager?.GetClassLogger<NettyDiscoveryHandler>() ?? throw new ArgumentNullException(nameof(logManager));
    private readonly IDiscoveryMsgListener _discoveryMsgListener = discoveryManager ?? throw new ArgumentNullException(nameof(discoveryManager));
    private readonly IChannel _channel = channel ?? throw new ArgumentNullException(nameof(channel));
    private readonly IMessageSerializationService _msgSerializationService = msgSerializationService ?? throw new ArgumentNullException(nameof(msgSerializationService));
    private readonly ITimestamper _timestamper = timestamper ?? throw new ArgumentNullException(nameof(timestamper));
    private readonly NodeFilter[] _inboundMessageFilters = inboundMessageFilter is null
            ? CreateDefaultInboundMessageFilters()
            : [inboundMessageFilter];

    public override void ChannelActive(IChannelHandlerContext context) => OnChannelActivated?.Invoke(this, EventArgs.Empty);

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

        _ = LogDisconnectFailureAsync(context.DisconnectAsync());
    }

    public override void ChannelReadComplete(IChannelHandlerContext context) => context.Flush();

    public async Task SendMsg(DiscoveryMsg discoveryMsg)
    {
        IByteBuffer msgBuffer;
        try
        {
            if (_logger.IsTrace) _logger.Trace($"Sending message: {discoveryMsg}");
            msgBuffer = Serialize(discoveryMsg, _channel.Allocator);
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

    private bool TryParseMessage(DatagramPacket packet, out DiscoveryMsg? msg, out bool shouldForward)
    {
        msg = null;
        shouldForward = true;

        IByteBuffer content = packet.Content;
        EndPoint address = packet.Sender;

        int size = content.ReadableBytes;
        using ArrayPoolDisposableReturn handle = ArrayPoolDisposableReturn.Rent(size, out byte[] msgBytes);

        content.ReadBytes(msgBytes, 0, size);

        Interlocked.Add(ref Metrics.DiscoveryBytesReceived, size);

        if (size < 98)
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

        if (address is IPEndPoint remoteEndpoint && !TryAcceptInbound(remoteEndpoint))
        {
            if (_logger.IsDebug) _logger.Debug($"Rate limiting discovery message {type} from {remoteEndpoint}");
            shouldForward = false;
            return false;
        }

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
        if (!TryParseMessage(packet, out DiscoveryMsg? msg, out bool shouldForward) || msg == null)
        {
            if (shouldForward)
            {
                packet.Content.ResetReaderIndex();
                ctx.FireChannelRead(packet.Retain());
            }
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
                static state =>
                {
                    (IDiscoveryMsgListener discoveryMsgListener, DiscoveryMsg discoveryMsg) = ((IDiscoveryMsgListener, DiscoveryMsg))state!;
                    discoveryMsgListener.OnIncomingMsg(discoveryMsg);
                },
                (_discoveryMsgListener, msg),
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

    private DiscoveryMsg Deserialize(MsgType type, ArraySegment<byte> msg) => type switch
    {
        MsgType.Ping => _msgSerializationService.Deserialize<PingMsg>(msg),
        MsgType.Pong => _msgSerializationService.Deserialize<PongMsg>(msg),
        MsgType.FindNode => _msgSerializationService.Deserialize<FindNodeMsg>(msg),
        MsgType.Neighbors => _msgSerializationService.Deserialize<NeighborsMsg>(msg),
        MsgType.EnrRequest => _msgSerializationService.Deserialize<EnrRequestMsg>(msg),
        MsgType.EnrResponse => _msgSerializationService.Deserialize<EnrResponseMsg>(msg),
        _ => throw new Exception($"Unsupported messageType: {type}")
    };

    private IByteBuffer Serialize(DiscoveryMsg msg, IByteBufferAllocator? allocator) => msg.MsgType switch
    {
        MsgType.Ping => _msgSerializationService.ZeroSerialize((PingMsg)msg, allocator),
        MsgType.Pong => _msgSerializationService.ZeroSerialize((PongMsg)msg, allocator),
        MsgType.FindNode => _msgSerializationService.ZeroSerialize((FindNodeMsg)msg, allocator),
        MsgType.Neighbors => _msgSerializationService.ZeroSerialize((NeighborsMsg)msg, allocator),
        MsgType.EnrRequest => _msgSerializationService.ZeroSerialize((EnrRequestMsg)msg, allocator),
        MsgType.EnrResponse => _msgSerializationService.ZeroSerialize((EnrResponseMsg)msg, allocator),
        _ => throw new Exception($"Unsupported messageType: {msg.MsgType}")
    };

    private bool ValidateMsg(DiscoveryMsg msg, MsgType type, EndPoint address, IChannelHandlerContext ctx, DatagramPacket packet, int size)
    {
        long timeToExpire = msg.ExpirationTime - _timestamper.UnixTime.SecondsLong;
        if (timeToExpire < 0)
        {
            if (NetworkDiagTracer.IsEnabled) NetworkDiagTracer.ReportIncomingMessage(msg.FarAddress, "disc v4", $"{msg.MsgType} expired", size);
            if (_logger.IsDebug) _logger.Debug($"Received a discovery message that has expired {-timeToExpire} seconds ago, type: {type}, sender: {address}, message: {msg}");
            return false;
        }

        if (timeToExpire > MaxFutureExpirationOffset.TotalSeconds)
        {
            if (NetworkDiagTracer.IsEnabled) NetworkDiagTracer.ReportIncomingMessage(msg.FarAddress, "disc v4", $"{msg.MsgType} far future", size);
            if (_logger.IsDebug) _logger.Debug($"Received a discovery message that expires too far in the future ({timeToExpire} seconds), type: {type}, sender: {address}, message: {msg}");
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

    private bool TryAcceptInbound(IPEndPoint remoteEndpoint)
    {
        // Allow a small burst from the same IP so split Neighbors and other valid
        // multi-packet exchanges are not dropped before signature verification.
        NodeFilter[] inboundMessageFilters = _inboundMessageFilters;
        for (int i = 0; i < inboundMessageFilters.Length; i++)
        {
            if (inboundMessageFilters[i].TryAccept(remoteEndpoint.Address, exactOnly: true))
            {
                return true;
            }
        }

        return false;
    }

    private static NodeFilter[] CreateDefaultInboundMessageFilters()
    {
        NodeFilter[] filters = new NodeFilter[DefaultInboundMessageBurstPerIp];
        for (int i = 0; i < filters.Length; i++)
        {
            filters[i] = NodeFilter.CreateExact(DefaultInboundMessageFilterSize, DefaultInboundMessageWindow);
        }

        return filters;
    }

    private async Task LogDisconnectFailureAsync(Task disconnectTask)
    {
        try
        {
            await disconnectTask;
        }
        catch (Exception e)
        {
            if (_logger.IsTrace) _logger.Trace($"Error while disconnecting on context on {this} : {e}");
        }
    }

    public event EventHandler? OnChannelActivated;
}
