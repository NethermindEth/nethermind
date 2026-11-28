// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Buffers;
using DotNetty.Transport.Channels.Embedded;
using Nethermind.Network.Rlpx.Sockets;
using NUnit.Framework;

namespace Nethermind.Network.Test.Rlpx.Sockets;

public class OutboundBytesTests
{
    [Test]
    public void Stops_at_the_high_watermark_and_resumes_at_the_low_one()
    {
        OutboundBytes bytes = new();

        bytes.Add(OutboundBytes.HighWaterMark - 1);
        Assert.That(bytes.IsWritable, Is.True, "just under the high watermark");
        bytes.Add(1);
        Assert.That(bytes.IsWritable, Is.False, "at the high watermark");
        bytes.Add(-(OutboundBytes.HighWaterMark - OutboundBytes.LowWaterMark - 1));
        Assert.That(bytes.IsWritable, Is.False, "just over the low watermark");
        bytes.Add(-1);
        Assert.That(bytes.IsWritable, Is.True, "at the low watermark");
    }

    [Test]
    public void Stays_unwritable_once_closed()
    {
        OutboundBytes bytes = new();

        bytes.Close();
        bytes.Add(-OutboundBytes.HighWaterMark);

        Assert.That(bytes.IsWritable, Is.False);
    }

    /// <summary>
    /// The watermarks and comparisons are DotNetty's, with the marks <see cref="Rlpx.RlpxHost"/> sets: a channel stops
    /// being writable once its pending bytes reach the high watermark.
    /// </summary>
    [Test]
    public void Matches_dotnetty_at_the_high_watermark()
    {
        EmbeddedChannel channel = new();
        channel.Configuration.WriteBufferHighWaterMark = (int)OutboundBytes.HighWaterMark;
        channel.Configuration.WriteBufferLowWaterMark = (int)OutboundBytes.LowWaterMark;
        OutboundBytes bytes = new();

        _ = channel.WriteAsync(Unpooled.WrappedBuffer(new byte[OutboundBytes.HighWaterMark - 1]));
        bytes.Add(OutboundBytes.HighWaterMark - 1);
        Assert.That(bytes.IsWritable, Is.EqualTo(channel.IsWritable).And.True);

        _ = channel.WriteAsync(Unpooled.WrappedBuffer(new byte[1]));
        bytes.Add(1);
        Assert.That(bytes.IsWritable, Is.EqualTo(channel.IsWritable).And.False);

        channel.FinishAndReleaseAll();
    }
}
