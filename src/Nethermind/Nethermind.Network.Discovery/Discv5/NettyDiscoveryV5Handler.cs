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
        var udpPacket = new UdpReceiveResult(msg.Content.ReadAllBytesAsArray(), (IPEndPoint)msg.Sender);
        if (!_inboundQueue.Writer.TryWrite(udpPacket) && _logger.IsWarn)
            _logger.Warn("Skipping discovery v5 message as inbound buffer is full");
    }

    public async Task SendAsync(byte[] data, IPEndPoint destination)
    {
        if (_nettyChannel == null) throw new("Channel for discovery v5 is not initialized");

        var packet = new DatagramPacket(Unpooled.WrappedBuffer(data), destination);

        try
        {
            await _nettyChannel.WriteAndFlushAsync(packet);
        }
        catch (SocketException exception)
        {
            if (_logger.IsError) _logger.Error("Error sending data", exception);
            throw;
        }
    }

    public IAsyncEnumerable<UdpReceiveResult> ReadMessagesAsync(CancellationToken token = default) =>
        _inboundQueue.Reader.ReadAllAsync(token);

    public Task ListenAsync(CancellationToken token = default) => Task.CompletedTask;
    public void Close() => _inboundQueue.Writer.Complete();

    public static void Register(IServiceCollection services) => services
        .AddSingleton<NettyDiscoveryV5Handler>()
        .AddSingleton<IUdpConnection>(static p => p.GetRequiredService<NettyDiscoveryV5Handler>());
}
