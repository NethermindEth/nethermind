// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test;

public class SpmcRingBufferTests
{
    [Test]
    public void HasReadyItem_TracksPublishedItems()
    {
        SpmcRingBuffer<int> jobQueue = new(4);

#pragma warning disable NUnit2045 // Keep this state-changing operation separate from its prerequisites, dependent observations, or subsequent mutations.
        Assert.That(jobQueue.HasReadyItem, Is.False);
#pragma warning restore NUnit2045

        Assert.That(jobQueue.TryEnqueue(1), Is.True);
        Assert.That(jobQueue.HasReadyItem, Is.True);

        Assert.That(jobQueue.TryDequeue(out int item), Is.True);
        Assert.That(item, Is.EqualTo(1));
        Assert.That(jobQueue.HasReadyItem, Is.False);

        for (int i = 0; i < 4; i++)
        {
#pragma warning disable NUnit2045 // The call establishes an output or mutable state needed by later assertions; keep the success prerequisite fail-fast.
            Assert.That(jobQueue.TryEnqueue(i), Is.True);
#pragma warning restore NUnit2045
            Assert.That(jobQueue.TryDequeue(out item), Is.True);
            Assert.That(item, Is.EqualTo(i));
            Assert.That(jobQueue.HasReadyItem, Is.False);
        }

#pragma warning disable NUnit2045 // Keep this state-changing operation separate from its prerequisites, dependent observations, or subsequent mutations.
        Assert.That(jobQueue.TryEnqueue(42), Is.True);
#pragma warning restore NUnit2045
        Assert.That(jobQueue.HasReadyItem, Is.True);
    }

    [Test]
    public void SmokeTest()
    {
        SpmcRingBuffer<int> jobQueue = new(16);

        jobQueue.TryEnqueue(1);
        jobQueue.TryEnqueue(2);
        jobQueue.TryEnqueue(3);
        jobQueue.TryEnqueue(4);
        jobQueue.TryEnqueue(5);

#pragma warning disable NUnit2045 // Keep prerequisite acquisition outside a grouped scope; its output is used by later assertions or subsequent setup/disposal.
        Assert.That(jobQueue.TryDequeue(out int j), Is.True);
#pragma warning restore NUnit2045
        Assert.That(j, Is.EqualTo(1));
        Assert.That(jobQueue.TryDequeue(out j), Is.True);
        Assert.That(j, Is.EqualTo(2));
        Assert.That(jobQueue.TryDequeue(out j), Is.True);
        Assert.That(j, Is.EqualTo(3));
        Assert.That(jobQueue.TryDequeue(out j), Is.True);
        Assert.That(j, Is.EqualTo(4));
        Assert.That(jobQueue.TryDequeue(out j), Is.True);
        Assert.That(j, Is.EqualTo(5));
    }

    [Test]
    public void RollingSmokeTest()
    {
        SpmcRingBuffer<int> jobQueue = new(16);

        jobQueue.TryEnqueue(1);
        jobQueue.TryEnqueue(2);
        jobQueue.TryEnqueue(3);
        jobQueue.TryEnqueue(4);
        jobQueue.TryEnqueue(5);

        int j = 0;
        for (int i = 0; i < 100; i++)
        {
#pragma warning disable NUnit2045 // The call establishes an output or mutable state needed by later assertions; keep the success prerequisite fail-fast.
            Assert.That(jobQueue.TryDequeue(out j), Is.True);
#pragma warning restore NUnit2045
            Assert.That(j, Is.EqualTo(i + 1));
            Assert.That(jobQueue.TryEnqueue(i + 5 + 1), Is.True);
        }
    }

    [Test]
    public void SmokeTestFullAndRolling()
    {
        SpmcRingBuffer<int> jobQueue = new(16);

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
    public void TryDequeue_ClearsReferenceContainingEntry()
    {
        SpmcRingBuffer<ReferenceJob> jobQueue = new(4);
        ReferenceJob job = new(new object());

#pragma warning disable NUnit2045 // The call establishes an output or mutable state needed by later assertions; keep the success prerequisite fail-fast.
        Assert.That(jobQueue.TryEnqueue(job), Is.True);
#pragma warning restore NUnit2045
        Assert.That(jobQueue.TryDequeue(out ReferenceJob dequeued), Is.True);
        Assert.That(dequeued, Is.EqualTo(job));

        ReferenceJob[] entries = GetEntries(jobQueue);
        Assert.That(entries[0].Value, Is.Null);
    }

    [Test]
    public async Task HighConcurrency_StressTest_NoDataLoss()
    {
        int Capacity = 1024;
        int ItemsToProduce = 1_000_000;
        int ConsumerCount = 4;

        SpmcRingBuffer<int> buffer = new(Capacity);
        int[] consumedCounts = new int[ItemsToProduce];
        long totalConsumed = 0;

        // Producer Task (Single Producer)
        Task producer = Task.Run(() =>
        {
            for (int i = 0; i < ItemsToProduce; i++)
            {
                while (!buffer.TryEnqueue(i))
                {
                    Thread.SpinWait(10); // Wait for space
                }
            }
        });

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

        await Task.WhenAll(producer);
        await Task.WhenAll(consumers);

        // Assertions
        Assert.That(ItemsToProduce, Is.EqualTo(Interlocked.Read(ref totalConsumed)));

        for (int i = 0; i < ItemsToProduce; i++)
        {
            Assert.That(consumedCounts[i] == 1, $"Item {i} was consumed {consumedCounts[i]} times!");
        }
    }

    private static T[] GetEntries<T>(SpmcRingBuffer<T> buffer)
    {
        FieldInfo? entriesField = typeof(SpmcRingBuffer<T>).GetField("_entries", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.That(entriesField, Is.Not.Null);
        return (T[])entriesField!.GetValue(buffer)!;
    }

    private readonly record struct ReferenceJob(object? Value);
}
