// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using Nethermind.State;
using NUnit.Framework;

namespace Nethermind.Store.Test;

public class SpmcRingBufferTests
{

    [Test]
    public void SmokeTest()
    {
        SpmcRingBuffer<int> jobQueue = new SpmcRingBuffer<int>(16);

        jobQueue.TryEnqueue(1);
        jobQueue.TryEnqueue(2);
        jobQueue.TryEnqueue(3);
        jobQueue.TryEnqueue(4);
        jobQueue.TryEnqueue(5);

        jobQueue.TryDequeue(out int j).Should().BeTrue();
        j.Should().Be(1);
        jobQueue.TryDequeue(out j).Should().BeTrue();
        j.Should().Be(2);
        jobQueue.TryDequeue(out j).Should().BeTrue();
        j.Should().Be(3);
        jobQueue.TryDequeue(out j).Should().BeTrue();
        j.Should().Be(4);
        jobQueue.TryDequeue(out j).Should().BeTrue();
        j.Should().Be(5);
    }

    [Test]
    public void RollingSmokeTest()
    {
        SpmcRingBuffer<int> jobQueue = new SpmcRingBuffer<int>(16);

        jobQueue.TryEnqueue(1);
        jobQueue.TryEnqueue(2);
        jobQueue.TryEnqueue(3);
        jobQueue.TryEnqueue(4);
        jobQueue.TryEnqueue(5);

        int j = 0;
        for (int i = 0; i < 100; i++)
        {
            jobQueue.TryDequeue(out j).Should().BeTrue();
            j.Should().Be(i + 1);
            jobQueue.TryEnqueue(i + 5 + 1).Should().BeTrue();
        }
    }
}
