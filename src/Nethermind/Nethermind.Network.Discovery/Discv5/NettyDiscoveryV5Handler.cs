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
public class NettyDiscoveryV5Handler(ILogManager loggerManager) : NettyDiscoveryBaseHandler(loggerManager)
{
    private const int MaxMessagesBuffered = 1024;

    private readonly ILogger _logger = loggerManager.GetClassLogger<NettyDiscoveryV5Handler>();
    private readonly Channel<DatagramPacket> _inboundQueue = Channel.CreateBounded<DatagramPacket>(MaxMessagesBuffered);

    private IChannel? _nettyChannel;
    private int _activeReaders;

    public void InitializeChannel(IChannel channel) => _nettyChannel = channel;

    public override void ChannelActive(IChannelHandlerContext context) => OnChannelActivated?.Invoke(this, EventArgs.Empty);

    public override void ChannelInactive(IChannelHandlerContext context)
    {
        Close();
        base.ChannelInactive(context);
    }

    public override void HandlerRemoved(IChannelHandlerContext context)
    {
        Close();
        base.HandlerRemoved(context);
    }

    protected override void ChannelRead0(IChannelHandlerContext ctx, DatagramPacket msg)
    {
        msg.Retain();
        DatagramPacket queuedPacket = msg;

        if (_inboundQueue.Writer.TryWrite(queuedPacket))
        {
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
        if (_nettyChannel is null) throw new("Channel for discovery v5 is not initialized");

        DatagramPacket packet = new(Unpooled.WrappedBuffer(data), destination);

        try
        {
            await _nettyChannel.WriteAndFlushAsync(packet);
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

            return new PooledUdpReceiveResult((IPEndPoint)packet.Sender, buffer);
        }
        catch
        {
            buffer.Dispose();
            throw;
        }
    }

    public Task ListenAsync(CancellationToken token = default) => Task.CompletedTask;
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

    public event EventHandler? OnChannelActivated;
}
