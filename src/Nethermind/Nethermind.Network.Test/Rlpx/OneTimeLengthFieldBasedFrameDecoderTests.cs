// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Buffers;
using DotNetty.Transport.Channels;
using Nethermind.Core.Extensions;
using Nethermind.Network.Rlpx;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Network.Test.Rlpx;

public class OneTimeLengthFieldBasedFrameDecoderTests
{
    [Test]
    public void Will_pass_frame_only_once()
    {
        OneTimeLengthFieldBasedFrameDecoder frameDecoder = new();
        IChannelHandlerContext ctx = Substitute.For<IChannelHandlerContext>();

        frameDecoder.ChannelRead(ctx, Unpooled.CopiedBuffer(Bytes.FromHexString("0x0001ff")));
        frameDecoder.ChannelRead(ctx, Unpooled.CopiedBuffer(Bytes.FromHexString("0x0001ff")));

        ctx.Received(1).FireChannelRead(Arg.Is(Unpooled.CopiedBuffer(Bytes.FromHexString("0x0001ff"))));
    }
}
