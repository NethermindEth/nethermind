// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using DotNetty.Buffers;
using DotNetty.Common.Utilities;
using DotNetty.Transport.Channels;
using DotNetty.Transport.Channels.Sockets;
using FastEnumUtility;
using Nethermind.Core;
using Nethermind.Core.Buffers;
using Nethermind.Core.Collections;
using Nethermind.Core.Extensions;
using Nethermind.Logging;
using Nethermind.Network.Discovery.Discv4.Messages;
using ILogger = Nethermind.Logging.ILogger;

namespace Nethermind.Network.Discovery.Discv4;

public class NettyDiscoveryHandler(
    IDiscoveryMsgListener? discoveryManager,
    IChannel? channel,
    IMessageSerializationService? msgSerializationService,
    ITimestamper? timestamper,
    ILogManager? logManager,
    NodeFilter? inboundMessageFilter = null,
    int? globalInboundMessageBurst = null,
    int? inboundMessageQueueCapacity = null,
    int? inboundMessageWorkerCount = null) : NettyDiscoveryBaseHandler(logManager, channel ?? throw new ArgumentNullException(nameof(channel))), IMsgSender
{
    private static readonly TimeSpan MaxFutureExpirationOffset = TimeSpan.FromHours(1);
    private static readonly TimeSpan DefaultInboundMessageWindow = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan DefaultGlobalInboundMessageWindow = TimeSpan.FromMilliseconds(100);
    private const int DefaultInboundMessageBurstPerIp = 8;
    private const int DefaultInboundMessageFilterSize = 8_192;
    private const int DefaultGlobalInboundMessageBurst = 512;
    private const int DefaultInboundMessageQueueCapacity = 1_024;
    private const int DefaultInboundMessageWorkerCount = 4;
    private readonly ILogger _logger = logManager?.GetClassLogger<NettyDiscoveryHandler>() ?? throw new ArgumentNullException(nameof(logManager));
    private readonly IDiscoveryMsgListener _discoveryMsgListener = discoveryManager ?? throw new ArgumentNullException(nameof(discoveryManager));
    private readonly IMessageSerializationService _msgSerializationService = msgSerializationService ?? throw new ArgumentNullException(nameof(msgSerializationService));
    private readonly ITimestamper _timestamper = timestamper ?? throw new ArgumentNullException(nameof(timestamper));
    private readonly AddressBurstLimiter _inboundMessageLimiter = inboundMessageFilter is null
        ? new(DefaultInboundMessageBurstPerIp, DefaultInboundMessageFilterSize, DefaultInboundMessageWindow)
        : new(inboundMessageFilter);
    private readonly FixedWindowLimiter _globalInboundMessageLimiter = new(Math.Max(1, globalInboundMessageBurst ?? DefaultGlobalInboundMessageBurst), DefaultGlobalInboundMessageWindow);
    private readonly Channel<InboundDiscoveryPacket> _inboundMessages = System.Threading.Channels.Channel.CreateBounded<InboundDiscoveryPacket>(
        new BoundedChannelOptions(Math.Max(1, inboundMessageQueueCapacity ?? DefaultInboundMessageQueueCapacity))
        {
            SingleReader = false,
            SingleWriter = false
        });
    private readonly int _inboundMessageWorkerCount = Math.Max(1, inboundMessageWorkerCount ?? DefaultInboundMessageWorkerCount);
    private int _dispatchWorkersStarted;

    protected override void CloseInbound() => _inboundMessages.Writer.TryComplete();

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
            msgBuffer = Serialize(discoveryMsg, Channel.Allocator);
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
            await Channel.WriteAndFlushAsync(packet);
        }
        catch (Exception e)
        {
            if (_logger.IsTrace) _logger.Trace($"Error when sending a discovery message Msg: {discoveryMsg} ,Exp: {e}");
        }

        Interlocked.Add(ref Metrics.DiscoveryBytesSent, size);
        Metrics.DiscoveryMessagesSent.Increment(discoveryMsg.MsgType);
    }

    private bool TryAcceptPacket(DatagramPacket packet, out MsgType type, out bool shouldForward, out EndPoint address)
    {
        type = default;
        shouldForward = true;

        IByteBuffer content = packet.Content;
        // Dual-stack sockets report IPv4 senders as IPv4-mapped IPv6 addresses ("::ffff:x.x.x.x");
        // normalize here, at the point of receipt, so nothing derived from it carries this form (mirrors NettyDiscoveryV5Handler.NormalizeEndpoint).
        address = packet.Sender is IPEndPoint senderEndpoint ? NormalizeEndpoint(senderEndpoint) : packet.Sender;

        int size = content.ReadableBytes;
        Interlocked.Add(ref Metrics.DiscoveryBytesReceived, size);

        if (size < 98)
        {
            if (_logger.IsDebug) _logger.Debug($"Incorrect discovery message, length: {size}, sender: {address}");
            return false;
        }

        int readerIndex = content.ReaderIndex;
        byte msgTypeByte = content.GetByte(readerIndex + 97);
        if (FromMsgTypeByte(msgTypeByte) is not { } resolvedType)
        {
            if (_logger.IsDebug) _logger.Debug($"Unsupported message type: {msgTypeByte}, sender: {address}");
            return false;
        }

        type = resolvedType;
        shouldForward = false;
        if (_logger.IsTrace) _logger.Trace($"Received message: {type}");

        if (!_globalInboundMessageLimiter.TryAcquire())
        {
            if (_logger.IsDebug) _logger.Debug($"Rate limiting discovery message globally, type: {type}, sender: {address}");
            return false;
        }

        if (address is IPEndPoint remoteEndpoint && !TryAcceptInbound(remoteEndpoint))
        {
            if (_logger.IsDebug) _logger.Debug($"Rate limiting discovery message {type} from {remoteEndpoint}");
            return false;
        }

        return true;
    }

    protected override void ChannelRead0(IChannelHandlerContext ctx, DatagramPacket packet)
    {
        if (!TryAcceptPacket(packet, out MsgType type, out bool shouldForward, out EndPoint address))
        {
            if (shouldForward)
            {
                packet.Content.ResetReaderIndex();
                ctx.FireChannelRead(packet.Retain());
            }
            return;
        }

        int size = packet.Content.ReadableBytes;
        EnsureDispatchWorkersStarted();

        packet.Retain();
        if (!_inboundMessages.Writer.TryWrite(new InboundDiscoveryPacket(ctx, packet, type, address, size)))
        {
            ReferenceCountUtil.Release(packet);
            if (_logger.IsDebug) _logger.Debug($"Dropping discovery message because inbound dispatch queue is full, type: {type}, sender: {address}");
        }
    }

    protected virtual MsgType? FromMsgTypeByte(byte b) =>
        FastEnum.IsDefined((MsgType)b) ? (MsgType)b : null;

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

    private bool ValidateMsg(DiscoveryMsg msg, MsgType type, EndPoint address, int size)
    {
        if (msg is not EnrResponseMsg)
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
        }

        if (msg.FarAddress is null)
        {
            if (NetworkDiagTracer.IsEnabled) NetworkDiagTracer.ReportIncomingMessage(msg.FarAddress, "disc v4", $"{msg.MsgType} has null far address", size);
            if (_logger.IsDebug) _logger.Debug($"Discovery message without a valid far address {msg.FarAddress}, type: {type}, sender: {address}, message: {msg}");
            return false;
        }

        if (!msg.FarAddress.Equals(address))
        {
            if (NetworkDiagTracer.IsEnabled) NetworkDiagTracer.ReportIncomingMessage(msg.FarAddress, "disc v4", $"{msg.MsgType} has incorrect far address", size);
            if (_logger.IsDebug) _logger.Debug($"Discovery fake IP detected - pretended {msg.FarAddress}, type: {type}, sender: {address}, message: {msg}");
            return false;
        }

        if (msg.FarPublicKey is null)
        {
            if (NetworkDiagTracer.IsEnabled) NetworkDiagTracer.ReportIncomingMessage(msg.FarAddress, "disc v4", $"{msg.MsgType} has null far public key", size);
            if (_logger.IsDebug) _logger.Debug($"Discovery message without a valid signature {msg.FarAddress}, type: {type}, sender: {address}, message: {msg}");
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

    // Allow a small burst from the same IP so split Neighbors and other valid
    // multi-packet exchanges are not dropped before signature verification.
    private bool TryAcceptInbound(IPEndPoint remoteEndpoint)
        => _inboundMessageLimiter.TryAccept(remoteEndpoint.Address);

    private static IPEndPoint NormalizeEndpoint(IPEndPoint endpoint)
        => endpoint.Address.IsIPv4MappedToIPv6
            ? new IPEndPoint(endpoint.Address.MapToIPv4(), endpoint.Port)
            : endpoint;

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

    private async Task ProcessInboundMessagesAsync()
    {
        try
        {
            await foreach (InboundDiscoveryPacket packet in _inboundMessages.Reader.ReadAllAsync())
            {
                try
                {
                    ProcessInboundMessage(packet);
                }
                catch (Exception e)
                {
                    if (_logger.IsError) _logger.Error($"Error while dispatching discovery message, type: {packet.Type}, sender: {packet.Address}", e);
                }
                finally
                {
                    ReferenceCountUtil.Release(packet.Packet);
                }
            }
        }
        catch (Exception e)
        {
            if (_logger.IsError) _logger.Error("Error in discovery message dispatch loop", e);
        }
    }

    private void ProcessInboundMessage(InboundDiscoveryPacket packet)
    {
        if (!TryDeserialize(packet, out DiscoveryMsg? msg))
        {
            ForwardPacket(packet);
            return;
        }

        ReportMsgByType(msg, packet.Size);

        if (!ValidateMsg(msg, packet.Type, packet.Address, packet.Size))
        {
            ForwardPacket(packet);
            return;
        }

        // Discv4 request handling can wait for response packets that must be decoded by this same bounded queue.
        DispatchMessage(msg);
    }

    private void DispatchMessage(DiscoveryMsg msg)
    {
        Task dispatchTask = _discoveryMsgListener.OnIncomingMsg(msg);
        if (!dispatchTask.IsCompletedSuccessfully)
        {
            _ = ObserveDispatchFailure(dispatchTask, msg);
        }
    }

    private async Task ObserveDispatchFailure(Task dispatchTask, DiscoveryMsg msg)
    {
        try
        {
            await dispatchTask;
        }
        catch (Exception e)
        {
            if (_logger.IsError) _logger.Error($"Error while handling discovery message, type: {msg.MsgType}, sender: {msg.FarAddress}", e);
        }
    }

    private bool TryDeserialize(InboundDiscoveryPacket packet, [NotNullWhen(true)] out DiscoveryMsg? msg)
    {
        msg = null;
        IByteBuffer content = packet.Packet.Content;
        int readerIndex = content.ReaderIndex;
        using ArrayPoolDisposableReturn handle = ArrayPoolDisposableReturn.Rent(packet.Size, out byte[] msgBytes);
        content.GetBytes(readerIndex, msgBytes, 0, packet.Size);

        try
        {
            msg = Deserialize(packet.Type, new ArraySegment<byte>(msgBytes, 0, packet.Size));
            msg.FarAddress = (IPEndPoint)packet.Address;
            return true;
        }
        catch (Exception e)
        {
            if (_logger.IsDebug) _logger.Debug($"Error during deserialization of the message, type: {packet.Type}, sender: {packet.Address}, msg: {msgBytes.AsSpan(0, packet.Size).ToHexString()}, {e.Message}");
            return false;
        }
    }

    private static void ForwardPacket(InboundDiscoveryPacket packet)
    {
        packet.Packet.Content.ResetReaderIndex();
        packet.Context.FireChannelRead(packet.Packet.Retain());
    }

    private void EnsureDispatchWorkersStarted()
    {
        if (Interlocked.Exchange(ref _dispatchWorkersStarted, 1) != 0)
        {
            return;
        }

        for (int i = 0; i < _inboundMessageWorkerCount; i++)
        {
            _ = Task.Run(ProcessInboundMessagesAsync);
        }
    }

    private sealed class FixedWindowLimiter(int maxCount, TimeSpan window)
    {
        private readonly Lock _lock = new();
        private long _windowStartTicks = Stopwatch.GetTimestamp();
        private int _count;

        public bool TryAcquire()
        {
            lock (_lock)
            {
                long now = Stopwatch.GetTimestamp();
                if (Stopwatch.GetElapsedTime(_windowStartTicks, now) >= window)
                {
                    _windowStartTicks = now;
                    _count = 0;
                }

                if (_count >= maxCount)
                {
                    return false;
                }

                _count++;
                return true;
            }
        }
    }

    private readonly record struct InboundDiscoveryPacket(IChannelHandlerContext Context, DatagramPacket Packet, MsgType Type, EndPoint Address, int Size);
}
