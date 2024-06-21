// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net;
using System.Net.Sockets;
using DotNetty.Transport.Channels;
using DotNetty.Transport.Channels.Sockets;
using Lantern.Discv5.WireProtocol.Packet;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.Discovery;

public class NettyDiscoveryV5Handler : SimpleChannelInboundHandler<DatagramPacket>
{
    private readonly IPacketManager _packetManager;

    public NettyDiscoveryV5Handler(IPacketManager packetManager)
    {
        _packetManager = packetManager;
    }

    protected override void ChannelRead0(IChannelHandlerContext ctx, DatagramPacket msg)
    {
        var udpPacket = new UdpReceiveResult(msg.Content.ReadAllBytesAsArray(), (IPEndPoint) msg.Sender);

        // Explicitly run it on the default scheduler to prevent something down the line hanging netty task scheduler.
        Task.Factory.StartNew(
            () => _packetManager.HandleReceivedPacket(udpPacket),
            CancellationToken.None,
            TaskCreationOptions.RunContinuationsAsynchronously,
            TaskScheduler.Default
        );
    }
}
