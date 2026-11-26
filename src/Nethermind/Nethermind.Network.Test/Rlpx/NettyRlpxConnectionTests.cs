// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using DotNetty.Buffers;
using DotNetty.Transport.Channels;
using DotNetty.Transport.Channels.Embedded;
using Nethermind.Logging;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.ProtocolHandlers;
using Nethermind.Network.Rlpx;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Network.Test.Rlpx;

[Parallelizable(ParallelScope.All)]
public class NettyRlpxConnectionTests
{
    [Test]
    public async Task Is_not_writable_once_the_channel_is_closed()
    {
        EmbeddedChannel channel = new();
        NettyRlpxConnection connection = new(channel, LimboLogs.Instance);
        Assert.That(connection.IsWritable, Is.True, "Open channel");

        await channel.CloseAsync();
        Assert.That(connection.IsWritable, Is.False, "Closed channel");
    }

    [Test]
    public void Is_not_writable_above_the_high_watermark()
    {
        EmbeddedChannel channel = new();
        NettyRlpxConnection connection = new(channel, LimboLogs.Instance);

        _ = channel.WriteAsync(Unpooled.WrappedBuffer(new byte[channel.Configuration.WriteBufferHighWaterMark + 1]));
        Assert.That(connection.IsWritable, Is.False, "Above the high watermark");

        channel.Flush();
        Assert.That(connection.IsWritable, Is.True, "Flushed below the low watermark");
        channel.FinishAndReleaseAll();
    }

    [Test]
    public void Can_enable_snappy_only_with_outbound_splitter([Values] bool hasSplitter)
    {
        IChannel channel = Substitute.For<IChannel>();
        IChannelPipeline pipeline = Substitute.For<IChannelPipeline>();
        channel.Pipeline.Returns(pipeline);
        ZeroNettyP2PHandler handler = new(Substitute.For<ISession>(), LimboLogs.Instance);
        pipeline.Get<ZeroNettyP2PHandler>().Returns(handler);
        pipeline.Get<ZeroPacketSplitter>().Returns(hasSplitter ? new ZeroPacketSplitter() : null);
        NettyRlpxConnection connection = new(channel, LimboLogs.Instance);
        Assert.That(handler.SnappyEnabled, Is.False);

        if (hasSplitter)
        {
            Assert.That(connection.EnableSnappy, Throws.Nothing);
        }
        else
        {
            Assert.That(connection.EnableSnappy, Throws.InvalidOperationException.With.Message.Contains(nameof(ZeroPacketSplitter)));
        }

        Assert.That(handler.SnappyEnabled, Is.True);
        pipeline.Received().Get<ZeroPacketSplitter>();
        pipeline.DidNotReceive().AddBefore(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<ZeroSnappyEncoder>());
    }

    [Test]
    public async Task Disconnect_closes_the_channel_and_releases_unsent_data()
    {
        EmbeddedChannel channel = new();
        NettyRlpxConnection connection = new(channel, LimboLogs.Instance);
        IByteBuffer unsent = Unpooled.Buffer(1).WriteByte(1);
        _ = channel.WriteAsync(unsent);

        await connection.DisconnectAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(channel.Open, Is.False, "Open");
            Assert.That(unsent.ReferenceCount, Is.Zero, "Unsent data reference count");
        }
    }
}
