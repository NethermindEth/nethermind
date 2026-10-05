// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using DotNetty.Buffers;
using FastEnumUtility;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Extensions;
using Nethermind.Logging;
using Nethermind.Network.Discovery.Discv4.Messages;
using Nethermind.Serialization.Rlp;
using ILogger = Nethermind.Logging.ILogger;

namespace Nethermind.Network.Discovery.Discv4;

public class DiscoveryHandler(
    IDiscoveryMsgListener? discoveryManager,
    IMessageSerializationService? msgSerializationService,
    ITimestamper? timestamper,
    ILogManager? logManager,
    NodeFilter? inboundMessageFilter = null,
    int? globalInboundMessageBurst = null,
    int? inboundMessageQueueCapacity = null,
    int? inboundMessageWorkerCount = null) : IMsgSender
{
    private const int MaxPacketSize = DiscoveryConnectionsPool.MaxPacketSize;
    private static readonly TimeSpan MaxFutureExpirationOffset = TimeSpan.FromHours(1);
    private static readonly TimeSpan DefaultInboundMessageWindow = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan DefaultGlobalInboundMessageWindow = TimeSpan.FromMilliseconds(100);
    private const int DefaultInboundMessageBurstPerIp = 8;
    private const int DefaultInboundMessageFilterSize = 8_192;
    private const int DefaultGlobalInboundMessageBurst = 512;
    private const int DefaultInboundMessageQueueCapacity = 1_024;
    private const int DefaultInboundMessageWorkerCount = 4;
    private readonly ILogger _logger = logManager?.GetClassLogger<DiscoveryHandler>() ?? throw new ArgumentNullException(nameof(logManager));
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
    private IDatagramSocket? _socket;
    private Action<PooledUdpReceiveResult>? _forward;

    private IDatagramSocket Socket => _socket ?? throw new InvalidOperationException("Discovery channel is not initialized.");

    /// <summary>
    /// Attaches the handler to the discovery socket.
    /// </summary>
    /// <param name="socket">The socket used to send messages.</param>
    /// <param name="forward">Receives datagrams that are not valid discv4 messages, taking ownership of them.</param>
    internal void InitializeChannel(IDatagramSocket socket, Action<PooledUdpReceiveResult> forward)
    {
        _socket = socket;
        _forward = forward;
    }

    internal void CloseInbound() => _inboundMessages.Writer.TryComplete();

    public async Task SendMsg(DiscoveryMsg discoveryMsg)
    {
        IDatagramSocket socket = Socket;
        IByteBuffer msgBuffer;
        try
        {
            if (_logger.IsTrace) TraceSending(discoveryMsg);
            msgBuffer = Serialize(discoveryMsg, NethermindBuffers.DiscoveryAllocator);
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

        try
        {
            await socket.SendToAsync(msgBuffer.ReadAllBytesAsMemory(), discoveryMsg.FarAddress!);
        }
        catch (Exception e)
        {
            if (_logger.IsTrace) TraceSendFailure(discoveryMsg, e);
        }
        finally
        {
            msgBuffer.Release();
        }

        Interlocked.Add(ref Metrics.DiscoveryBytesSent, size);
        Metrics.DiscoveryMessagesSent.Increment(discoveryMsg.MsgType);
        Metrics.DiscoveryMessagesSentByProtocol.Increment(new DiscoveryMessageKey("discv4", FastEnum.GetName(discoveryMsg.MsgType)!));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void TraceSending(DiscoveryMsg message) => _logger.Trace($"Sending message: {message}");

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void TraceSendFailure(DiscoveryMsg message, Exception exception) =>
        _logger.Trace($"Error when sending a discovery message Msg: {message} ,Exp: {exception}");

    private bool TryAcceptPacket(PooledUdpReceiveResult packet, out MsgType type, out bool shouldForward)
    {
        type = default;
        shouldForward = true;

        ReadOnlySpan<byte> content = packet.Buffer.Span;
        IPEndPoint address = packet.RemoteEndPoint;

        int size = content.Length;

        if (size < 98)
        {
            if (_logger.IsTrace) TraceNonDiscv4Message(size, address);
            return false;
        }

        byte msgTypeByte = content[97];
        if (FromMsgTypeByte(msgTypeByte) is not { } resolvedType)
        {
            if (_logger.IsTrace) TraceUnsupportedMessageType(msgTypeByte, address);
            return false;
        }

        type = resolvedType;
        shouldForward = false;
        if (_logger.IsTrace) _logger.Trace($"Received message: {type}");

        if (!_globalInboundMessageLimiter.TryAcquire())
        {
            if (_logger.IsTrace) _logger.Trace($"Rate limiting discovery message globally, type: {type}, sender: {address}");
            return false;
        }

        if (!TryAcceptInbound(address))
        {
            if (_logger.IsTrace) _logger.Trace($"Rate limiting discovery message {type} from {address}");
            return false;
        }

        return true;

        [MethodImpl(MethodImplOptions.NoInlining)]
        void TraceNonDiscv4Message(int messageSize, EndPoint sender) =>
            _logger.Trace($"Forwarding non-discv4 discovery message, length: {messageSize}, sender: {sender}");

        [MethodImpl(MethodImplOptions.NoInlining)]
        void TraceUnsupportedMessageType(byte messageType, EndPoint sender) =>
            _logger.Trace($"Unsupported message type: {messageType}, sender: {sender}");
    }

    /// <summary>
    /// Handles a datagram received on the discovery socket, taking ownership of it.
    /// </summary>
    internal void Receive(PooledUdpReceiveResult packet)
    {
        if (!TryAcceptPacket(packet, out MsgType type, out bool shouldForward))
        {
            if (shouldForward)
            {
                Forward(packet);
            }
            else
            {
                packet.Dispose();
            }
            return;
        }

        EnsureDispatchWorkersStarted();

        if (!_inboundMessages.Writer.TryWrite(new InboundDiscoveryPacket(packet, type)))
        {
            packet.Dispose();
            if (_logger.IsDebug) _logger.Debug($"Dropping discovery message because inbound dispatch queue is full, type: {type}, sender: {packet.RemoteEndPoint}");
        }
    }

    protected virtual MsgType? FromMsgTypeByte(byte b) =>
        FastEnum.IsDefined((MsgType)b) ? (MsgType)b : null;

    private DiscoveryMsg Deserialize(MsgType type, IByteBuffer msg) => type switch
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
                if (_logger.IsTrace) TraceExpiredMessage(-timeToExpire, type, address, msg);
                return false;
            }

            if (timeToExpire > MaxFutureExpirationOffset.TotalSeconds)
            {
                if (NetworkDiagTracer.IsEnabled) NetworkDiagTracer.ReportIncomingMessage(msg.FarAddress, "disc v4", $"{msg.MsgType} far future", size);
                if (_logger.IsTrace) TraceFarFutureMessage(timeToExpire, type, address, msg);
                return false;
            }
        }

        if (msg.FarAddress is null)
        {
            if (NetworkDiagTracer.IsEnabled) NetworkDiagTracer.ReportIncomingMessage(msg.FarAddress, "disc v4", $"{msg.MsgType} has null far address", size);
            if (_logger.IsTrace) TraceMissingFarAddress(type, address, msg);
            return false;
        }

        if (!msg.FarAddress.Equals(address))
        {
            if (NetworkDiagTracer.IsEnabled) NetworkDiagTracer.ReportIncomingMessage(msg.FarAddress, "disc v4", $"{msg.MsgType} has incorrect far address", size);
            if (_logger.IsTrace) TraceFakeIp(type, address, msg);
            return false;
        }

        if (msg.FarPublicKey is null)
        {
            if (NetworkDiagTracer.IsEnabled) NetworkDiagTracer.ReportIncomingMessage(msg.FarAddress, "disc v4", $"{msg.MsgType} has null far public key", size);
            if (_logger.IsTrace) TraceMissingPublicKey(type, address, msg);
            return false;
        }

        return true;

        [MethodImpl(MethodImplOptions.NoInlining)]
        void TraceExpiredMessage(long secondsAgo, MsgType messageType, EndPoint sender, DiscoveryMsg message) =>
            _logger.Trace($"Received a discovery message that has expired {secondsAgo} seconds ago, type: {messageType}, sender: {sender}, message: {message}");

        [MethodImpl(MethodImplOptions.NoInlining)]
        void TraceFarFutureMessage(long seconds, MsgType messageType, EndPoint sender, DiscoveryMsg message) =>
            _logger.Trace($"Received a discovery message that expires too far in the future ({seconds} seconds), type: {messageType}, sender: {sender}, message: {message}");

        [MethodImpl(MethodImplOptions.NoInlining)]
        void TraceMissingFarAddress(MsgType messageType, EndPoint sender, DiscoveryMsg message) =>
            _logger.Trace($"Discovery message without a valid far address {message.FarAddress}, type: {messageType}, sender: {sender}, message: {message}");

        [MethodImpl(MethodImplOptions.NoInlining)]
        void TraceFakeIp(MsgType messageType, EndPoint sender, DiscoveryMsg message) =>
            _logger.Trace($"Discovery fake IP detected - pretended {message.FarAddress}, type: {messageType}, sender: {sender}, message: {message}");

        [MethodImpl(MethodImplOptions.NoInlining)]
        void TraceMissingPublicKey(MsgType messageType, EndPoint sender, DiscoveryMsg message) =>
            _logger.Trace($"Discovery message without a valid signature {message.FarAddress}, type: {messageType}, sender: {sender}, message: {message}");
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

    private async Task ProcessInboundMessagesAsync()
    {
        try
        {
            await foreach (InboundDiscoveryPacket packet in _inboundMessages.Reader.ReadAllAsync())
            {
                bool forwarded = false;
                try
                {
                    forwarded = ProcessInboundMessage(packet);
                }
                catch (Exception e)
                {
                    if (_logger.IsError) _logger.Error($"Error while dispatching discovery message, type: {packet.Type}, sender: {packet.Address}", e);
                }
                finally
                {
                    if (!forwarded)
                    {
                        packet.Packet.Dispose();
                    }
                }
            }
        }
        catch (Exception e)
        {
            if (_logger.IsError) _logger.Error("Error in discovery message dispatch loop", e);
        }
    }

    /// <returns><c>true</c> when the packet was forwarded, passing on its ownership.</returns>
    private bool ProcessInboundMessage(InboundDiscoveryPacket packet)
    {
        if (!TryDeserialize(packet, out DiscoveryMsg? msg))
        {
            Forward(packet.Packet);
            return true;
        }

        ReportMsgByType(msg, packet.Size);

        if (!ValidateMsg(msg, packet.Type, packet.Address, packet.Size))
        {
            Forward(packet.Packet);
            return true;
        }

        // Discv4 request handling can wait for response packets that must be decoded by this same bounded queue.
        DispatchMessage(msg);
        return false;
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
        if (!MemoryMarshal.TryGetArray(packet.Packet.Buffer, out ArraySegment<byte> segment))
        {
            return false;
        }

        IByteBuffer msgBuffer = Unpooled.WrappedBuffer(segment.Array, segment.Offset, segment.Count);

        try
        {
            msg = Deserialize(packet.Type, msgBuffer);
            msg.FarAddress = packet.Address;
            return true;
        }
        catch (Exception e)
        {
            if (_logger.IsTrace) TraceDeserializationFailure(packet, e);
            return false;
        }
        finally
        {
            msgBuffer.Release();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        void TraceDeserializationFailure(InboundDiscoveryPacket failedPacket, Exception exception) =>
            _logger.Trace($"Error during deserialization of the message, type: {failedPacket.Type}, sender: {failedPacket.Address}, msg: {failedPacket.Packet.Buffer.Span.ToHexString()}, {exception.Message}");
    }

    private void Forward(PooledUdpReceiveResult packet)
    {
        if (_forward is null)
        {
            packet.Dispose();
            return;
        }

        _forward(packet);
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

    private readonly record struct InboundDiscoveryPacket(PooledUdpReceiveResult Packet, MsgType Type)
    {
        public IPEndPoint Address => Packet.RemoteEndPoint;

        public int Size => Packet.Buffer.Length;
    }
}
