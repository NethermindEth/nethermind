// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test;

public class MpmcRingBufferTests
{
    [Test]
    public void SmokeTest()
    {
        MpmcRingBuffer<int> jobQueue = new MpmcRingBuffer<int>(16);

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
        MpmcRingBuffer<int> jobQueue = new MpmcRingBuffer<int>(16);

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

    [Test]
    public void SmokeTestFullAndRolling()
    {
        MpmcRingBuffer<int> jobQueue = new MpmcRingBuffer<int>(16);

        for (int i = 0; i < 16; i++)
        {
            Assert.That(jobQueue.TryEnqueue(1), Is.True);
        }
        Assert.That(jobQueue.TryEnqueue(1), Is.False);

        for (int i = 0; i < 16; i++)
        {
            Assert.That(jobQueue.TryDequeue(out _), Is.True);
        }
        Assert.That(jobQueue.TryDequeue(out _), Is.False);

        for (int i = 0; i < 16; i++)
        {
            Assert.That(jobQueue.TryEnqueue(1), Is.True);
        }
        Assert.That(jobQueue.TryEnqueue(1), Is.False);

        for (int i = 0; i < 16; i++)
        {
            Assert.That(jobQueue.TryDequeue(out _), Is.True);
        }
        Assert.That(jobQueue.TryDequeue(out _), Is.False);
    }

    [Test]
    public async Task HighConcurrency_StressTest_NoDataLoss()
    {
        int Capacity = 1024;
        int ItemsToProduce = 1_000_000;
        int ProducerCount = 4;
        int ConsumerCount = 4;

        MpmcRingBuffer<int> buffer = new MpmcRingBuffer<int>(Capacity);
        int[] consumedCounts = new int[ItemsToProduce];
        long totalConsumed = 0;

        // Producer Task (Single Producer)
        long itemLeftToProduce = ItemsToProduce;

        // Producers Tasks (Multiple Producers)
        Task[] producers = Enumerable.Range(0, ProducerCount).Select(_ => Task.Run(() =>
        {
            while (true)
            {
                long remaining = Interlocked.Read(ref itemLeftToProduce);
                if (remaining == 0) break;
                if (Interlocked.CompareExchange(ref itemLeftToProduce, remaining - 1, remaining) != remaining) continue;

                while (!buffer.TryEnqueue((int)remaining - 1))
                {
                    Thread.SpinWait(10); // Wait for space
                }
            }
        })).ToArray();

        // Consumer Tasks (Multiple Consumers)
        Task[] consumers = Enumerable.Range(0, ConsumerCount).Select(_ => Task.Run(() =>
        {
            while (Interlocked.Read(ref totalConsumed) < ItemsToProduce)
            {
                if (buffer.TryDequeue(out int item))
                {
                    // Track that this specific item was hit
                    Interlocked.Increment(ref consumedCounts[item]);
                    Interlocked.Increment(ref totalConsumed);
                }
                else
                {
                    Thread.SpinWait(10);
                }
            }
        })).ToArray();

        await Task.WhenAll(producers);
        await Task.WhenAll(consumers);

        // Assertions
        Assert.That(ItemsToProduce, Is.EqualTo(Interlocked.Read(ref totalConsumed)));

        for (int i = 0; i < ItemsToProduce; i++)
        {
            Assert.That(consumedCounts[i] == 1, $"Item {i} was consumed {consumedCounts[i]} times!");
        }
    }
}
