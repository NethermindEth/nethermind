// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using DotNetty.Buffers;
using DotNetty.Transport.Channels;
using FluentAssertions;
using Nethermind.Logging;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.Messages;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P;

public class RateLimitedPacketSenderTests
{
    // Due to timing inconsistencies we use a margin of error.
    private readonly TimeSpan _epsilon = TimeSpan.FromMilliseconds(10);

    [TestCase(1, 10, 0)]
    [TestCase(1, 10, 10)]
    [TestCase(100, 500, 500)]
    [TestCase(500, 100, 1000)]
    public void all_messages_are_always_sent(int messageCount, int byteBudget, int throttleTimeMillis)
    {
        IByteBuffer serialized = Substitute.For<IByteBuffer>();
        IMessageSerializationService serializer = Substitute.For<IMessageSerializationService>();
        serializer.ZeroSerialize(PingMessage.Instance).Returns(serialized);

        IChannelHandlerContext context = Substitute.For<IChannelHandlerContext>();
        IChannel channel = Substitute.For<IChannel>();
        channel.Active.Returns(true);
        context.Channel.Returns(channel);

        TimeSpan throttleTime = TimeSpan.FromMilliseconds(throttleTimeMillis);
        RateLimitedPacketSender packetSender = new(byteBudget, throttleTime, serializer, LimboLogs.Instance);

        packetSender.Init();
        packetSender.HandlerAdded(context);

        for (int i = 0; i < messageCount; i++)
        {
            packetSender.Enqueue(PingMessage.Instance);
        }
        packetSender.Dispose();

        context.Received(messageCount).WriteAndFlushAsync(Arg.Any<IByteBuffer>());
    }

    [TestCase(2, 1000, 1000)]
    [TestCase(4, 5000, 1000)]
    public void messages_are_throttled_when_exceed_byte_budget(int messageCount, int messageSize, int byteBudget)
    {
        IByteBuffer serialized = Substitute.For<IByteBuffer>();
        serialized.ReadableBytes.Returns(messageSize);
        IMessageSerializationService serializer = Substitute.For<IMessageSerializationService>();
        serializer.ZeroSerialize(PingMessage.Instance).Returns(serialized);

        List<TimeSpan> times = new();
        Stopwatch stopwatch = new();
        IChannelHandlerContext context = Substitute.For<IChannelHandlerContext>();
        IChannel channel = Substitute.For<IChannel>();
        channel.Active.Returns(true);
        context.Channel.Returns(channel);
        context
            .When(c => c.WriteAndFlushAsync(Arg.Any<object>()))
            .Do(_ => times.Add(stopwatch.Elapsed));

        TimeSpan throttleTime = TimeSpan.FromMilliseconds(500);
        RateLimitedPacketSender packetSender = new(byteBudget, throttleTime, serializer, LimboLogs.Instance);

        packetSender.Init();
        packetSender.HandlerAdded(context);
        stopwatch.Start();

        for (int i = 0; i < messageCount; i++)
        {
            packetSender.Enqueue(PingMessage.Instance);
        }
        packetSender.Dispose();

        context.Received(messageCount).WriteAndFlushAsync(Arg.Any<IByteBuffer>());
        TimeSpan last = times[0];
        for (int i = 1; i < times.Count; i++)
        {
            TimeSpan current = times[i];

            TimeSpan delta = (times[i] - last) + _epsilon;
            delta.Should().BeGreaterOrEqualTo(throttleTime);

            last = current;
        }
    }

    [TestCase(2, 500, 1000)]
    [TestCase(4, 100, 500)]
    public void messages_are_not_throttled_when_within_byte_budget(int messageCount, int messageSize, int byteBudget)
    {
        IByteBuffer serialized = Substitute.For<IByteBuffer>();
        serialized.ReadableBytes.Returns(messageSize);
        IMessageSerializationService serializer = Substitute.For<IMessageSerializationService>();
        serializer.ZeroSerialize(PingMessage.Instance).Returns(serialized);

        List<TimeSpan> times = new();
        Stopwatch stopwatch = new();
        IChannelHandlerContext context = Substitute.For<IChannelHandlerContext>();
        IChannel channel = Substitute.For<IChannel>();
        channel.Active.Returns(true);
        context.Channel.Returns(channel);
        context
            .When(c => c.WriteAndFlushAsync(Arg.Any<object>()))
            .Do(_ => times.Add(stopwatch.Elapsed));

        TimeSpan throttleTime = TimeSpan.FromMilliseconds(500);
        RateLimitedPacketSender packetSender = new(byteBudget, throttleTime, serializer, LimboLogs.Instance);

        packetSender.Init();
        packetSender.HandlerAdded(context);
        stopwatch.Start();

        for (int i = 0; i < messageCount; i++)
        {
            packetSender.Enqueue(PingMessage.Instance);
        }
        packetSender.Dispose();

        context.Received(messageCount).WriteAndFlushAsync(Arg.Any<IByteBuffer>());
        TimeSpan last = times[0];
        for (int i = 1; i < times.Count; i++)
        {
            TimeSpan current = times[i];

            TimeSpan delta = times[i] - last;
            delta.Should().BeLessThanOrEqualTo(_epsilon);

            last = current;
        }
    }

    [Test]
    public void supports_multiple_dispose()
    {
        IMessageSerializationService serializer = Substitute.For<IMessageSerializationService>();
        RateLimitedPacketSender packetSender = new(100, TimeSpan.FromMilliseconds(500), serializer, LimboLogs.Instance);
        packetSender.Init();

        packetSender.Dispose();
        packetSender.Dispose();
    }

    [Test]
    public void supports_multiple_dispose_concurrent()
    {
        IMessageSerializationService serializer = Substitute.For<IMessageSerializationService>();
        RateLimitedPacketSender packetSender = new(100, TimeSpan.FromMilliseconds(500), serializer, LimboLogs.Instance);
        packetSender.Init();

        Parallel.For(0, 10, _ => packetSender.Dispose());
    }
}
