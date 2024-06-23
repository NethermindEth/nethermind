// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Buffers;
using DotNetty.Transport.Bootstrapping;
using DotNetty.Transport.Channels;
using DotNetty.Transport.Channels.Sockets;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Network.Config;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.Discovery;

public class RebindBootstrap: Bootstrap
{

    #region Overrides of Bootstrap

    protected override void Init(IChannel channel)
    {
        base.Init(channel);
    }

    #endregion

}

public class MultiVersionInboundDiscoveryHandler : SimpleChannelInboundHandler<DatagramPacket>
{
    private readonly INetworkConfig _networkConfig;

    public MultiVersionInboundDiscoveryHandler(INetworkConfig networkConfig)
    {
        _networkConfig = networkConfig;
    }

    private IChannelHandler? _handlerV4;
    private IChannelHandler? _handlerV5;

    public void AddHandlerV4(IChannelHandler handler) => _handlerV4 = handler;
    public void AddHandlerV5(IChannelHandler handler) => _handlerV5 = handler;

    protected override void ChannelRead0(IChannelHandlerContext ctx, DatagramPacket msg)
    {
        IChannelHandler? handler = _handlerV4 != null && IsDiscoveryV4Packet(msg)
            ? _handlerV4
            : _handlerV5;

        handler?.ChannelRead(ctx, msg);
    }

    // TODO find a faster/simpler/more-reliable way
    // TODO move to DiscoveryApp?
    private bool IsDiscoveryV4Packet(DatagramPacket packet)
    {
        try
        {
            IByteBuffer msg = packet.Content;
            if (msg.ReadableBytes < 98) return false;

            Memory<byte> msgBytes = msg.ReadAllBytesAsMemory();
            Memory<byte> mdc = msgBytes[..32];
            Span<byte> sigAndData = msgBytes.Span[32..];
            Span<byte> computedMdc = ValueKeccak.Compute(sigAndData).BytesAsSpan;

            return Bytes.AreEqual(mdc.Span, computedMdc);
        }
        catch (Exception)
        {
            return false;
        }
    }
}
