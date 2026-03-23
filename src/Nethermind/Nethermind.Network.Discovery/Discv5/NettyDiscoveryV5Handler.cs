// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using DotNetty.Buffers;
using DotNetty.Transport.Channels;
using DotNetty.Transport.Channels.Sockets;
using Lantern.Discv5.WireProtocol.Connection;
using Microsoft.Extensions.DependencyInjection;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.Discovery;

/// <summary>
/// Adapter, integrating DotNetty externally-managed <see cref="IChannel"/> with Lantern.Discv5
/// </summary>
public class NettyDiscoveryV5Handler : NettyDiscoveryBaseHandler, IUdpConnection
{
    private const int MaxMessagesBuffered = 1024;

    private readonly ILogger _logger;
    private readonly Channel<UdpReceiveResult> _inboundQueue;

    private IChannel? _nettyChannel;

    public NettyDiscoveryV5Handler(ILogManager loggerManager) : base(loggerManager)
    {
        _logger = loggerManager.GetClassLogger<NettyDiscoveryV5Handler>();
        _inboundQueue = Channel.CreateBounded<UdpReceiveResult>(MaxMessagesBuffered);
    }

    public void InitializeChannel(IChannel channel) => _nettyChannel = channel;

    protected override void ChannelRead0(IChannelHandlerContext ctx, DatagramPacket msg)
    {
        IPEndPoint sender = NormalizeEndpoint((IPEndPoint)msg.Sender);
        UdpReceiveResult udpPacket = new(msg.Content.ReadAllBytesAsArray(), sender);

        if (!_inboundQueue.Writer.TryWrite(udpPacket) && _logger.IsDebug)
        {
            _logger.Warn("Skipping discovery v5 message as inbound buffer is full");
        }
    }

    public async Task SendAsync(byte[] data, IPEndPoint destination)
    {
        if (_nettyChannel == null) throw new("Channel for discovery v5 is not initialized");

        IPEndPoint normalizedDestination = NormalizeEndpoint(destination);
        DatagramPacket packet = new(Unpooled.WrappedBuffer(data), normalizedDestination);

        try
        {
            await _nettyChannel.WriteAndFlushAsync(packet);
        }
        catch (SocketException exception)
        {
            if (_logger.IsDebug) _logger.Error("DEBUG/ERROR Error sending data", exception);
            throw;
        }
    }

    private static IPEndPoint NormalizeEndpoint(IPEndPoint endpoint) =>
        endpoint.Address.IsIPv4MappedToIPv6
            ? new IPEndPoint(endpoint.Address.MapToIPv4(), endpoint.Port)
            : endpoint;

    public IAsyncEnumerable<UdpReceiveResult> ReadMessagesAsync(CancellationToken token = default) =>
        _inboundQueue.Reader.ReadAllAsync(token);

    public Task ListenAsync(CancellationToken token = default) => Task.CompletedTask;
    public void Close() => _inboundQueue.Writer.Complete();

    public static void Register(IServiceCollection services) => services
        .AddSingleton<NettyDiscoveryV5Handler>()
        .AddSingleton<IUdpConnection>(static p => p.GetRequiredService<NettyDiscoveryV5Handler>());
}
