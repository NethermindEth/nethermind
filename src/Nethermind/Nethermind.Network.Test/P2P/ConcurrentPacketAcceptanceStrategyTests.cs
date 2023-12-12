// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Threading;
using DotNetty.Buffers;
using FluentAssertions;
using Nethermind.Network.P2P;
using Nethermind.Network.Rlpx;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P;

public class ConcurrentPacketAcceptanceStrategyTests
{
    [TestCase(1000, 10, 9990)]
    public void can_decorate_thread_unsafe_implementation(int packetCount, int packetSize, int byteLimit)
    {
        IByteBuffer buffer = Substitute.For<IByteBuffer>();
        buffer.ReferenceCount.Returns(1);
        buffer.ReadableBytes.Returns(packetSize);
        ZeroPacket packet = new(buffer);

        IPacketAcceptanceStrategy strategy = new ConcurrentPacketAcceptanceStrategy(new RateLimitedPacketAcceptanceStrategy(byteLimit, TimeSpan.FromMilliseconds(10_000)));
        bool[] results = new bool[packetCount];
        Thread[] threads = new Thread[packetCount];
        for (int i = 0; i < packetCount; i++)
        {
            int idx = i;
            threads[idx] = new Thread(() => results[idx] = strategy.Accepts(packet));
            threads[idx].Start();
        }
        foreach (Thread thread in threads) { thread.Join(); }

        results.Count(accepted => accepted is false).Should().Be(1);
    }
}
