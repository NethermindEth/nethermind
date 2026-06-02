// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using DotNetty.Buffers;
using DotNetty.Common.Utilities;
using DotNetty.Transport.Channels;
using DotNetty.Transport.Channels.Sockets;
using Microsoft.Extensions.DependencyInjection;
using Nethermind.Core.Collections;
using Nethermind.Logging;

namespace Nethermind.Network.Discovery.Discv5;

/// <summary>
/// DotNetty UDP bridge used by the native discv5 implementation.
/// </summary>
public class NettyDiscoveryV5Handler(ILogManager loggerManager, IChannel? channel = null) : NettyDiscoveryBaseHandler(loggerManager, channel)
{
    private const int MaxMessagesBuffered = 1024;

    private readonly ILogger _logger = loggerManager.GetClassLogger<NettyDiscoveryV5Handler>();
    private readonly Channel<DatagramPacket> _inboundQueue = System.Threading.Channels.Channel.CreateBounded<DatagramPacket>(MaxMessagesBuffered);

    private int _activeReaders;

    protected override void CloseInbound() => Close();

    protected override void ChannelRead0(IChannelHandlerContext ctx, DatagramPacket msg)
    {
        msg.Retain();
        DatagramPacket queuedPacket = msg;

        if (_inboundQueue.Writer.TryWrite(queuedPacket))
        {
            if (_logger.IsTrace) _logger.Trace($"Queued discv5 UDP packet from {NormalizeEndpoint((IPEndPoint)msg.Sender)}, bytes: {msg.Content.ReadableBytes}.");
            return;
        }

        ReferenceCountUtil.Release(queuedPacket);
        if (_logger.IsWarn)
        {
            _logger.Warn("Skipping discovery v5 message as inbound buffer is full");
        }
    }

    public async Task SendAsync(byte[] data, IPEndPoint destination)
    {
        DatagramPacket packet = new(Unpooled.WrappedBuffer(data), destination);

        try
        {
            if (_logger.IsTrace) _logger.Trace($"Sending discv5 UDP packet to {destination}, bytes: {data.Length}.");
            await Channel.WriteAndFlushAsync(packet);
        }
        catch (SocketException exception)
        {
            _logger.DebugError("Error sending data", exception);
            throw;
        }
    }

    internal async IAsyncEnumerable<PooledUdpReceiveResult> ReadMessagesAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token = default)
    {
        Interlocked.Increment(ref _activeReaders);
        try
        {
            await foreach (DatagramPacket packet in _inboundQueue.Reader.ReadAllAsync(token))
            {
                PooledUdpReceiveResult receiveResult = default;
                bool hasReceiveResult = false;
                try
                {
                    receiveResult = CreateReceiveResult(packet);
                    hasReceiveResult = true;
                    yield return receiveResult;
                }
                finally
                {
                    if (hasReceiveResult)
                    {
                        receiveResult.Dispose();
                    }

                    ReferenceCountUtil.Release(packet);
                }
            }
        }
        finally
        {
            Interlocked.Decrement(ref _activeReaders);
            ReleaseQueuedPackets();
        }
    }

    private static PooledUdpReceiveResult CreateReceiveResult(DatagramPacket packet)
    {
        ArrayPoolSpan<byte> buffer = new(packet.Content.ReadableBytes);
        try
        {
            Span<byte> bytes = buffer;
            for (int i = 0; i < bytes.Length; i++)
            {
                bytes[i] = packet.Content.ReadByte();
            }

            return new PooledUdpReceiveResult(NormalizeEndpoint((IPEndPoint)packet.Sender), buffer);
        }
        catch
        {
            buffer.Dispose();
            throw;
        }
    }

    private static IPEndPoint NormalizeEndpoint(IPEndPoint endpoint)
        => endpoint.Address.IsIPv4MappedToIPv6
            ? new IPEndPoint(endpoint.Address.MapToIPv4(), endpoint.Port)
            : endpoint;

    public void Close()
    {
        _inboundQueue.Writer.TryComplete();
        if (Volatile.Read(ref _activeReaders) == 0)
        {
            ReleaseQueuedPackets();
        }
    }

    private void ReleaseQueuedPackets()
    {
        while (_inboundQueue.Reader.TryRead(out DatagramPacket? packet))
        {
            ReferenceCountUtil.Release(packet);
        }
    }

    public static void Register(IServiceCollection services) => services.AddSingleton<NettyDiscoveryV5Handler>();
}
