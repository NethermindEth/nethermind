// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Transport.Channels;
using DotNetty.Transport.Channels.Sockets;
using Nethermind.Logging;

namespace Nethermind.Network.Discovery;

public abstract class NettyDiscoveryBaseHandler : SimpleChannelInboundHandler<DatagramPacket>
{
    private readonly ILogger _logger;

    // https://github.com/ethereum/devp2p/blob/master/discv4.md#wire-protocol
    // https://github.com/ethereum/devp2p/blob/master/discv5/discv5-wire.md#udp-communication
    protected const int MaxPacketSize = 1280;

    protected NettyDiscoveryBaseHandler(ILogManager? logManager)
    {
        _logger = logManager?.GetClassLogger<NettyDiscoveryBaseHandler>() ?? throw new ArgumentNullException(nameof(logManager));
    }

    public override void ChannelRead(IChannelHandlerContext ctx, object msg)
    {
        if (msg is DatagramPacket packet && AcceptInboundMessage(packet) && !ValidatePacket(packet))
            return;

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
}
