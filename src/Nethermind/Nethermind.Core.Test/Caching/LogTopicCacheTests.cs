// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;
using NUnit.Framework;

namespace Nethermind.Core.Test.Caching;

[NonParallelizable]
public class LogTopicCacheTests
{
    [Test]
    public void Repeated_topic_returns_the_same_instance()
    {
        byte[] topic = TopicWithSeed(1);

        Hash256 first = LogTopicCache.Get(topic);
        Hash256 second = LogTopicCache.Get(topic);

        Assert.That(first.Bytes.SequenceEqual(topic), Is.True);
        Assert.That(second, Is.SameAs(first));
    }

    [Test]
    public void Topics_sharing_a_slot_never_return_each_others_value()
    {
        (byte[] a, byte[] b) = CollidingPair();

        Hash256 fromA = LogTopicCache.Get(a);
        Hash256 fromB = LogTopicCache.Get(b);
        Hash256 fromAAgain = LogTopicCache.Get(a);

        Assert.That(fromA.Bytes.SequenceEqual(a), Is.True);
        Assert.That(fromB.Bytes.SequenceEqual(b), Is.True);
        Assert.That(fromAAgain.Bytes.SequenceEqual(a), Is.True);
        Assert.That(fromAAgain, Is.Not.SameAs(fromB));
    }

    [Test]
    public void Concurrent_lookups_of_colliding_topics_always_return_the_requested_value()
    {
        const int threadCount = 4;
        const int iterations = 100_000;
        (byte[] a, byte[] b) = CollidingPair();
        int mismatches = 0;

        Thread[] threads = new Thread[threadCount];
        for (int t = 0; t < threadCount; t++)
        {
            byte[] own = t % 2 == 0 ? a : b;
            threads[t] = new Thread(() =>
            {
                for (int i = 0; i < iterations; i++)
                {
                    if (!LogTopicCache.Get(own).Bytes.SequenceEqual(own)) Interlocked.Increment(ref mismatches);
                }
            });
        }

        foreach (Thread thread in threads) thread.Start();
        foreach (Thread thread in threads) thread.Join();

        Assert.That(mismatches, Is.Zero);
    }

    internal static (byte[] A, byte[] B) CollidingPair()
    {
        byte[] a = TopicWithSeed(2);
        int slot = SlotOf(a);
        for (int seed = 3; ; seed++)
        {
            byte[] b = TopicWithSeed(seed);
            if (SlotOf(b) == slot) return (a, b);
        }
    }

    private static int SlotOf(byte[] topic) => new ValueHash256(topic).GetHashCode() & (LogTopicCache.Count - 1);

    private static byte[] TopicWithSeed(int seed) => Keccak.Compute(BitConverter.GetBytes(seed)).BytesToArray();
}
