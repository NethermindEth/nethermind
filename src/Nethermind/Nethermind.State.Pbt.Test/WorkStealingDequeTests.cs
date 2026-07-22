// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Pbt;
using NUnit.Framework;

namespace Nethermind.State.Pbt.Test;

public class WorkStealingDequeTests
{
    private sealed class Item(int index)
    {
        public int Index => index;
    }

    [Test]
    public void EmptyQueue_HandsNothingOut_AndAFullOneRefusesAPush()
    {
        WorkStealingDeque<Item> queue = new(2);

        Assert.That(queue.TryPopHead(), Is.Null);
        Assert.That(queue.TrySteal(), Is.Null);
        Assert.That(queue.Head, Is.Zero);

        Assert.That(queue.TryPushHead(new Item(0)), Is.True);
        Assert.That(queue.TryPushHead(new Item(1)), Is.True);
        Assert.That(queue.TryPushHead(new Item(2)), Is.False, "the queue holds two");
        Assert.That(queue.Head, Is.EqualTo(2), "a refused push leaves the queue where it was");

        // the owner takes the newest, a thief the oldest
        Assert.That(queue.TryPopHead()!.Index, Is.EqualTo(1));
        Assert.That(queue.TrySteal()!.Index, Is.EqualTo(0));
        Assert.That(queue.TryPopHead(), Is.Null);
    }

    /// <summary>
    /// The queue's whole contract under contention: an item it hands over goes to one thread and one
    /// only, and no item is lost on the way. A duplicate would have the updater fold one bucket twice —
    /// two writes of the same node, one of them from a released buffer — and a loss would leave a frame
    /// waiting on a job nothing runs.
    /// </summary>
    [TestCase(1)]
    [TestCase(4)]
    public void UnderStealing_EveryItemIsHandedOutExactlyOnce(int thiefCount)
    {
        const int capacity = 64;
        const int count = 200_000;

        WorkStealingDeque<Item> queue = new(capacity);
        Item[] items = new Item[count];
        for (int i = 0; i < count; i++) items[i] = new Item(i);

        ConcurrentBag<Item> stolen = [];
        using CancellationTokenSource cts = new();
        Task[] thieves = new Task[thiefCount];
        for (int i = 0; i < thiefCount; i++)
        {
            thieves[i] = Task.Run(() =>
            {
                while (!cts.IsCancellationRequested)
                {
                    if (queue.TrySteal() is { } item) stolen.Add(item);
                    else Thread.Yield();
                }
            });
        }

        // the owner pushes as fast as the queue takes them and pops its own back whenever it is full,
        // which is the shape a spawning frame leaves behind
        List<Item> taken = [];
        for (int pushed = 0; pushed < count;)
        {
            if (queue.TryPushHead(items[pushed])) pushed++;
            else if (queue.TryPopHead() is { } item) taken.Add(item);
        }

        while (queue.TryPopHead() is { } item) taken.Add(item);

        cts.Cancel();
        Task.WaitAll(thieves);

        // whatever a thief was mid-steal when it stopped is still in the queue
        while (queue.TryPopHead() is { } item) taken.Add(item);

        bool[] seen = new bool[count];
        foreach (Item item in taken) MarkOnce(seen, item);
        foreach (Item item in stolen) MarkOnce(seen, item);

        Assert.That(Array.IndexOf(seen, false), Is.EqualTo(-1), "every item must come out of the queue");
        Assert.That(taken.Count + stolen.Count, Is.EqualTo(count));
    }

    private static void MarkOnce(bool[] seen, Item item)
    {
        Assert.That(seen[item.Index], Is.False, $"item {item.Index} came out of the queue twice");
        seen[item.Index] = true;
    }
}
