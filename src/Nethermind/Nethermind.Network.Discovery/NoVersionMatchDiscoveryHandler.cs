// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Transport.Channels;
using DotNetty.Transport.Channels.Sockets;
using Nethermind.Logging;

namespace Nethermind.Network.Discovery;

public class NoVersionMatchDiscoveryHandler(ILogger logger) : SimpleChannelInboundHandler<DatagramPacket>
{
    protected override void ChannelRead0(IChannelHandlerContext ctx, DatagramPacket msg)
    {
        logger.Error($"Discovery message version is not matched: {msg}");
    }

    public void Add(IDatagramChannel channel) => channel.Pipeline.AddLast(this);
}
