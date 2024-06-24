// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using DotNetty.Buffers;
using DotNetty.Transport.Channels;
using DotNetty.Transport.Channels.Sockets;
using Lantern.Discv5.WireProtocol.Connection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.Discovery;

/// <summary>
/// Adapter, integrating DotNetty externally-managed <see cref="IChannel"/> with Lantern.Discv5
/// </summary>
public class NettyDiscoveryV5Handler : SimpleChannelInboundHandler<DatagramPacket>, IUdpConnection
{
    private readonly ILogger<NettyDiscoveryV5Handler> _logger;
    private readonly Channel<UdpReceiveResult> _inboundQueue;
    private readonly IByteBufferAllocator _bufferAllocator = PooledByteBufferAllocator.Default;

    private IChannel? _nettyChannel;

    public NettyDiscoveryV5Handler(ILoggerFactory loggerFactory)
    {
        _logger = loggerFactory.CreateLogger<NettyDiscoveryV5Handler>();
        _inboundQueue = Channel.CreateUnbounded<UdpReceiveResult>();
    }

    public void InitializeChannel(IChannel channel) => _nettyChannel = channel;

    protected override void ChannelRead0(IChannelHandlerContext ctx, DatagramPacket msg)
    {
        var udpPacket = new UdpReceiveResult(msg.Content.ReadAllBytesAsArray(), (IPEndPoint) msg.Sender);
        _inboundQueue.Writer.TryWrite(udpPacket);
    }

    public async Task SendAsync(byte[] data, IPEndPoint destination)
    {
        if (_nettyChannel == null) throw new("Channel for discovery v5 is not initialized.");

        UdpConnection.ValidatePacketSize(data);

        IByteBuffer packet = _bufferAllocator.Buffer(data.Length, data.Length);
        packet.WriteBytes(data);

        try
        {
            await _nettyChannel.WriteAndFlushAsync(packet);
        }
        catch (SocketException exception)
        {
            _logger.LogError(exception, "Error sending data");
            throw;
        }
    }

    public IAsyncEnumerable<UdpReceiveResult> ReadMessagesAsync(CancellationToken token = default) =>
        _inboundQueue.Reader.ReadAllAsync(token);

    public Task ListenAsync(CancellationToken token = default) => Task.CompletedTask;
    public void Close() => _inboundQueue.Writer.Complete();

    public static IServiceCollection Register(IServiceCollection services) => services
        .AddSingleton<NettyDiscoveryV5Handler>()
        .AddSingleton<IUdpConnection>(p => p.GetRequiredService<NettyDiscoveryV5Handler>());
}
