// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Common.Utilities;
using DotNetty.Transport.Channels;
using DotNetty.Transport.Channels.Sockets;
using Nethermind.Logging;

namespace Nethermind.Network.Discovery;

public abstract class NettyDiscoveryBaseHandler(ILogManager? logManager, IChannel? channel = null) : SimpleChannelInboundHandler<DatagramPacket>
{
    private readonly ILogger _logger = logManager?.GetClassLogger<NettyDiscoveryBaseHandler>() ?? throw new ArgumentNullException(nameof(logManager));
    private IChannel? _channel = channel;

    // https://github.com/ethereum/devp2p/blob/master/discv4.md#wire-protocol
    // https://github.com/ethereum/devp2p/blob/master/discv5/discv5-wire.md#udp-communication
    protected const int MaxPacketSize = 1280;

    protected IChannel Channel => _channel ?? throw new InvalidOperationException("Discovery channel is not initialized.");

    public void InitializeChannel(IChannel channel) => _channel = channel;

    public override void ChannelActive(IChannelHandlerContext context) => OnChannelActivated?.Invoke(this, EventArgs.Empty);

    public override void ChannelInactive(IChannelHandlerContext context)
    {
        CloseInbound();
        base.ChannelInactive(context);
    }

    public override void HandlerRemoved(IChannelHandlerContext context)
    {
        CloseInbound();
        base.HandlerRemoved(context);
    }

    public override void ChannelRead(IChannelHandlerContext ctx, object msg)
    {
        if (msg is DatagramPacket packet && AcceptInboundMessage(packet) && !ValidatePacket(packet))
        {
            ReferenceCountUtil.Release(msg);
            return;
        }

        base.ChannelRead(ctx, msg);
    }

    protected bool ValidatePacket(DatagramPacket packet)
    {
        // Potential cases where this can happen:
        // - Neighbors response containing 16+ nodes in a single packet
        if (packet.Content.ReadableBytes is 0 or > MaxPacketSize)
        {
            if (_logger.IsDebug) _logger.Debug($"Skipping discovery packet of invalid size: {packet.Content.ReadableBytes}");
            return false;
        }

        return true;
    }

    protected virtual void CloseInbound()
    {
    }

    public event EventHandler? OnChannelActivated;
}
