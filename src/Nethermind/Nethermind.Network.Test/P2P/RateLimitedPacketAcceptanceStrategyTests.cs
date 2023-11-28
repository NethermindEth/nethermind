// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using DotNetty.Buffers;
using FluentAssertions;
using Nethermind.Network.P2P;
using Nethermind.Network.Rlpx;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P;

public class RateLimitedPacketAcceptanceStrategyTests
{
    private readonly TimeSpan _epsilon = TimeSpan.FromMilliseconds(10);

    [TestCase(100, 100)]
    [TestCase(300, 300)]
    [TestCase(500, 500)]
    public void never_drops_first_packet(int byteLimit, int delayInMillis)
    {
        IByteBuffer buffer = Substitute.For<IByteBuffer>();
        buffer.ReferenceCount.Returns(1);
        ZeroPacket packet = new(buffer);
        IPacketAcceptanceStrategy strategy = new RateLimitedPacketAcceptanceStrategy(byteLimit, TimeSpan.FromMilliseconds(delayInMillis));

        strategy.Accepts(packet).Should().BeTrue();
    }

    [TestCase(1, 100, 1000)]
    [TestCase(2, 50, 100)]
    [TestCase(4, 25, 200)]
    public void does_not_drop_packets_when_within_byte_limit(int packetCount, int packetSize, int byteLimit)
    {
        IByteBuffer buffer = Substitute.For<IByteBuffer>();
        buffer.ReferenceCount.Returns(1);
        buffer.ReadableBytes.Returns(packetSize);
        ZeroPacket packet = new(buffer);

        IPacketAcceptanceStrategy strategy = new RateLimitedPacketAcceptanceStrategy(byteLimit, TimeSpan.FromMilliseconds(500));
        for (int i = 0; i < packetCount; i++)
        {
            strategy.Accepts(packet).Should().BeTrue();
        }
    }

    [TestCase(3, 50, 100, 2)]
    [TestCase(5, 100, 450, 4)]
    [TestCase(10, 50, 100, 2)]
    public void does_drop_packets_when_exceed_byte_limit(int packetCount, int packetSize, int byteLimit, int expectedToAccept)
    {
        IByteBuffer buffer = Substitute.For<IByteBuffer>();
        buffer.ReferenceCount.Returns(1);
        buffer.ReadableBytes.Returns(packetSize);
        ZeroPacket packet = new(buffer);

        IPacketAcceptanceStrategy strategy = new RateLimitedPacketAcceptanceStrategy(byteLimit, TimeSpan.FromMilliseconds(1000));
        for (int i = 0; i < expectedToAccept; i++)
        {
            strategy.Accepts(packet).Should().BeTrue();
        }
        int expectedToDrop = packetCount - expectedToAccept;
        for (int i = 0; i < expectedToDrop; i++)
        {
            strategy.Accepts(packet).Should().BeFalse();
        }
    }

    [TestCase(100)]
    [TestCase(300)]
    [TestCase(500)]
    public async Task drops_and_resumes_after_delay(int delayInMillis)
    {
        IByteBuffer buffer = Substitute.For<IByteBuffer>();
        buffer.ReferenceCount.Returns(1);
        buffer.ReadableBytes.Returns(100);
        ZeroPacket packet = new(buffer);

        TimeSpan throttleTime = TimeSpan.FromMilliseconds(delayInMillis);
        IPacketAcceptanceStrategy strategy = new RateLimitedPacketAcceptanceStrategy(200, throttleTime);

        strategy.Accepts(packet).Should().BeTrue();
        strategy.Accepts(packet).Should().BeTrue();
        strategy.Accepts(packet).Should().BeFalse();
        await Task.Delay(throttleTime + _epsilon);
        strategy.Accepts(packet).Should().BeTrue();
        strategy.Accepts(packet).Should().BeTrue();
    }
}
