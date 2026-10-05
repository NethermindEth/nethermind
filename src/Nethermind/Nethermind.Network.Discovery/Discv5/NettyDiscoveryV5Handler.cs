// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Runtime.InteropServices;
using DotNetty.Buffers;
using DotNetty.Transport.Channels;
using DotNetty.Transport.Channels.Sockets;
using Nethermind.Core.Collections;
using Nethermind.Logging;

namespace Nethermind.Network.Discovery.Discv5;

/// <summary>
/// DotNetty UDP bridge that connects the shared discovery channel to <see cref="DiscoveryV5Transport"/>.
/// </summary>
public sealed class NettyDiscoveryV5Handler : NettyDiscoveryBaseHandler
{
    private readonly ILogger _logger;
    private readonly DiscoveryV5Transport _transport;

    public NettyDiscoveryV5Handler(DiscoveryV5Transport transport, ILogManager loggerManager, IChannel? channel = null)
        : base(loggerManager, channel)
    {
        _logger = loggerManager.GetClassLogger<NettyDiscoveryV5Handler>();
        _transport = transport;
        _transport.BindSender(SendToChannel);
    }

    protected override void CloseInbound() => _transport.Close();

    protected override void ChannelRead0(IChannelHandlerContext ctx, DatagramPacket msg)
    {
        PooledUdpReceiveResult result = CreateReceiveResult(msg);
        int size = result.Buffer.Length;

        if (_transport.TryEnqueue(result))
        {
            if (_logger.IsTrace) _logger.Trace($"Queued discv5 UDP packet from {msg.Sender}, bytes: {size}.");
            return;
        }

        result.Dispose();
        if (_logger.IsWarn)
        {
            _logger.Warn("Skipping discovery v5 message as inbound buffer is full");
        }
    }

    private Task SendToChannel(byte[] data, IPEndPoint destination)
        => Channel.WriteAndFlushAsync(new DatagramPacket(Unpooled.WrappedBuffer(data), destination));

    private static PooledUdpReceiveResult CreateReceiveResult(DatagramPacket packet)
    {
        IByteBuffer content = packet.Content;
        int readerIndex = content.ReaderIndex;
        int readableBytes = content.ReadableBytes;
        ArrayPoolSpan<byte> buffer = new(readableBytes);
        try
        {
            if (!MemoryMarshal.TryGetArray(buffer.AsMemory(), out ArraySegment<byte> segment))
            {
                ThrowMissingArraySegment();
            }

            content.GetBytes(readerIndex, segment.Array!, segment.Offset, readableBytes);
            content.SetReaderIndex(readerIndex + readableBytes);

            return new PooledUdpReceiveResult(NormalizeEndpoint((IPEndPoint)packet.Sender), buffer);
        }
        catch
        {
            buffer.Dispose();
            throw;
        }

        [DoesNotReturn]
        static void ThrowMissingArraySegment()
            => throw new InvalidOperationException("Pooled UDP receive buffer must be array-backed.");
    }
}
