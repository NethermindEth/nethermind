// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net;
using System.Net.Sockets;
using DotNetty.Buffers;
using DotNetty.Transport.Channels;
using DotNetty.Transport.Channels.Sockets;
using Lantern.Discv5.WireProtocol.Connection;
using Lantern.Discv5.WireProtocol.Packet;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.Discovery;

/// <summary>
/// Adapter, integrating DotNetty externally-managed <see cref="IChannel"/> with Lantern.Discv5
/// </summary>
public class NettyDiscoveryV5Handler : SimpleChannelInboundHandler<DatagramPacket>,
    IUdpConnection, IConnectionManager
{
    private readonly ILogger<NettyDiscoveryV5Handler> _logger;
    private readonly IPacketManager _packetManager;
    private readonly IByteBufferAllocator _bufferAllocator = PooledByteBufferAllocator.Default;

    private IChannel? _channel;

    public NettyDiscoveryV5Handler(ILogger<NettyDiscoveryV5Handler> logger, IPacketManager packetManager)
    {
        _logger = logger;
        _packetManager = packetManager;
    }

    public void InitializeChannel(IChannel channel) => _channel = channel;

    protected override void ChannelRead0(IChannelHandlerContext ctx, DatagramPacket msg)
    {
        if (ctx.HasDiscoveryMessageVersion())
            return; // Already handled by previous protocol version

        var udpPacket = new UdpReceiveResult(msg.Content.ReadAllBytesAsArray(), (IPEndPoint) msg.Sender);

        // Explicitly run it on the default scheduler to prevent something down the line hanging netty task scheduler.
        Task.Factory.StartNew(
            () => _packetManager.HandleReceivedPacket(udpPacket),
            CancellationToken.None,
            TaskCreationOptions.RunContinuationsAsynchronously,
            TaskScheduler.Default
        );
    }

    public async Task SendAsync(byte[] data, IPEndPoint destination)
    {
        if (_channel == null) throw new("Channel for discovery v5 is not initialized.");

        UdpConnection.ValidatePacketSize(data);

        IByteBuffer packet = _bufferAllocator.Buffer(data.Length, data.Length);
        packet.WriteBytes(data);

        try
        {
            await _channel.WriteAndFlushAsync(packet);
        }
        catch (SocketException se)
        {
            _logger.LogError(se, "Error sending data");
            throw;
        }
    }

    // Messages are handled via Netty ChannelRead0 push-based override
    public IAsyncEnumerable<UdpReceiveResult> ReadMessagesAsync(CancellationToken token = default) => AsyncEnumerable.Empty<UdpReceiveResult>();

    // Connection management is expected to be handled by the caller using provided IChannel
    public Task ListenAsync(CancellationToken token = default) => Task.CompletedTask;
    public void Close() { }
    public void InitAsync() { }
    public Task StopConnectionManagerAsync() => Task.CompletedTask;

    public static IServiceCollection Register(IServiceCollection services)
    {
        services.AddSingleton<NettyDiscoveryV5Handler, NettyDiscoveryV5Handler>();
        services.AddSingleton<IUdpConnection>(p => p.GetRequiredService<NettyDiscoveryV5Handler>());
        services.AddSingleton<IConnectionManager>(p => p.GetRequiredService<NettyDiscoveryV5Handler>());
        return services;
    }
}
