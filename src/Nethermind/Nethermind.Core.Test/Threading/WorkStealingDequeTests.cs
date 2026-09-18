// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core.Threading;
using NUnit.Framework;

namespace Nethermind.Core.Test.Threading;

[Parallelizable(ParallelScope.All)]
public class WorkStealingDequeTests
{
    private sealed class NumberedJob(int number) : Rayon.Job
    {
        public int Number => number;

        public override void Execute() { }
    }

    [Test]
    public void Push_Pop_IsLifo_AndGrowsPastInitialCapacity()
    {
        const int count = 1000;
        Rayon.WorkStealingDeque deque = new();
        Assert.That(deque.Pop(), Is.Null);
        Assert.That(deque.TrySteal(out _), Is.EqualTo(Rayon.WorkStealingDeque.StealResult.Empty));

        for (int i = 0; i < count; i++)
        {
            deque.Push(new NumberedJob(i));
        }

        Assert.That(deque.IsEmpty, Is.False);
        Assert.That(deque.TrySteal(out Rayon.Job? stolen), Is.EqualTo(Rayon.WorkStealingDeque.StealResult.Success));
        Assert.That(((NumberedJob)stolen!).Number, Is.EqualTo(0));

        for (int i = count - 1; i >= 1; i--)
        {
            Assert.That(((NumberedJob)deque.Pop()!).Number, Is.EqualTo(i));
        }

        Assert.That(deque.Pop(), Is.Null);
        Assert.That(deque.IsEmpty, Is.True);
    }

    [Test]
    public void ConcurrentSteal_EveryJobTakenExactlyOnce([Values(1, 4, 16)] int thieves)
    {
        const int pushes = 200_000;
        Rayon.WorkStealingDeque deque = new();
        int[] taken = new int[pushes];
        int ownerTook = 0;
        int stolen = 0;
        int done = 0;

        Task[] thiefTasks = new Task[thieves];
        for (int t = 0; t < thieves; t++)
        {
            thiefTasks[t] = Task.Run(() =>
            {
                while (Volatile.Read(ref done) == 0 || !deque.IsEmpty)
                {
                    if (deque.TrySteal(out Rayon.Job? job) == Rayon.WorkStealingDeque.StealResult.Success)
                    {
                        Interlocked.Increment(ref taken[((NumberedJob)job!).Number]);
                        Interlocked.Increment(ref stolen);
                    }
                }
            });
        }

        // Owner: push in bursts and pop some back, so the deque is often at one element (the racy case).
        for (int i = 0; i < pushes; i++)
        {
            deque.Push(new NumberedJob(i));
            if ((i & 3) == 3)
            {
                Rayon.Job? popped = deque.Pop();
                if (popped is not null)
                {
                    Interlocked.Increment(ref taken[((NumberedJob)popped).Number]);
                    ownerTook++;
                }
            }
        }

        Rayon.Job? remaining;
        while ((remaining = deque.Pop()) is not null)
        {
            Interlocked.Increment(ref taken[((NumberedJob)remaining).Number]);
            ownerTook++;
        }

        Volatile.Write(ref done, 1);
        Task.WaitAll(thiefTasks);

        List<int> wrong = [];
        for (int i = 0; i < pushes; i++)
        {
            if (taken[i] != 1) wrong.Add(i);
        }

        Assert.That(wrong, Is.Empty);
        Assert.That(ownerTook + stolen, Is.EqualTo(pushes));
    }
}
