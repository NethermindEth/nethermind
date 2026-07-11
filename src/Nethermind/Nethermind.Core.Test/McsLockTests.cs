// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Threading;
using NUnit.Framework;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Nethermind.Core.Test;

public class MCSLockTests
{
    private McsLock mcsLock;

    [SetUp]
    public void Setup() => mcsLock = new McsLock();

    [Test]
    public void SingleThreadAcquireRelease()
    {
        using (McsLock.Disposable handle = mcsLock.Acquire())
        {
            Thread.Sleep(10);
        }

        Assert.Pass(); // Test passes if no deadlock or exception occurs.
    }

    [Test]
    public void MultipleThreads()
    {
        int counter = 0;
        int numberOfThreads = 10;
        List<Thread> threads = [];

        for (int i = 0; i < numberOfThreads; i++)
        {
            Thread thread = new(() =>
            {
                using McsLock.Disposable handle = mcsLock.Acquire();

                counter++;
            });
            threads.Add(thread);
            thread.Start();
        }

        foreach (Thread thread in threads)
        {
            thread.Join(); // Wait for all threads to complete.
        }

        Assert.That(counter, Is.EqualTo(numberOfThreads)); // Counter should equal the number of threads.
    }

    [Test]
    public void LockFairnessTest()
    {
        const int numberOfThreads = 10;
        List<int> executionOrder = [];
        List<Thread> threads = [];
        SemaphoreSlim[] gates = Enumerable.Range(0, numberOfThreads)
            .Select(_ => new SemaphoreSlim(0, 1)).ToArray();
        SemaphoreSlim[] reachedAcquire = Enumerable.Range(0, numberOfThreads)
            .Select(_ => new SemaphoreSlim(0, 1)).ToArray();

        using (McsLock.Disposable orchestrator = mcsLock.Acquire())
        {
            for (int i = 0; i < numberOfThreads; i++)
            {
                int threadId = i;
                Thread thread = new(() =>
                {
                    gates[threadId].Wait();
                    reachedAcquire[threadId].Release();
                    using McsLock.Disposable handle = mcsLock.Acquire();
                    executionOrder.Add(threadId);
                });
                threads.Add(thread);
                thread.Start();
            }

            for (int i = 0; i < numberOfThreads; i++)
            {
                gates[i].Release();
                reachedAcquire[i].Wait();
                Thread.Sleep(5);
            }
        }

        foreach (Thread thread in threads)
        {
            thread.Join();
        }

        List<int> expectedOrder = Enumerable.Range(0, numberOfThreads).ToList();
        Assert.That(executionOrder, Is.EqualTo(expectedOrder));
    }

    [Test]
    public void ReentrantAcquireThrows()
    {
        using McsLock.Disposable handle = mcsLock.Acquire();
        Assert.Throws<InvalidOperationException>(() => mcsLock.Acquire(),
            "a reentrant acquire must fail loud instead of self-deadlocking");
    }

    [Test]
    public void ReacquireAfterReleaseSucceeds()
    {
        for (int i = 0; i < 3; i++)
        {
            using McsLock.Disposable handle = mcsLock.Acquire();
        }

        Assert.Pass();
    }

    [Test]
    public void ParkedWaitersAreHandedTheLock()
    {
        // Observe every queued waiter entering Parked before releasing the orchestrator,
        // exercising the pulse path rather than only the spinning-head handoff.
        const int numberOfThreads = 4;
        int counter = 0;
        List<Thread> threads = [];
        McsLock.ThreadNode[] nodes = new McsLock.ThreadNode[numberOfThreads];
        using CountdownEvent nodesReady = new(numberOfThreads);

        using (McsLock.Disposable orchestrator = mcsLock.Acquire())
        {
            for (int i = 0; i < numberOfThreads; i++)
            {
                int threadId = i;
                Thread thread = new(() =>
                {
                    nodes[threadId] = mcsLock.CurrentThreadNodeForTesting;
                    nodesReady.Signal();
                    using McsLock.Disposable handle = mcsLock.Acquire();
                    Interlocked.Increment(ref counter);
                });
                threads.Add(thread);
                thread.Start();
            }

            nodesReady.Wait();
            foreach (McsLock.ThreadNode node in nodes)
            {
                Assert.That(
                    SpinWait.SpinUntil(
                        () => node.State == (nuint)McsLock.LockState.Parked,
                        TimeSpan.FromSeconds(10)),
                    Is.True,
                    "a waiter did not enter the parked state");
            }
        }

        foreach (Thread thread in threads)
        {
            Assert.That(thread.Join(TimeSpan.FromSeconds(10)), Is.True, "a parked waiter was never woken");
        }

        Assert.That(counter, Is.EqualTo(numberOfThreads));
    }

    [Test]
    public void StressMixedSpinAndParkHandoffs()
    {
        // Short and long holds interleaved across more threads than cores: exercises
        // spinning-head handoffs, parked handoffs, and empty-queue release races together.
        const int threadCount = 16;
        const int iterations = 2_000;
        long inside = 0;
        long total = 0;
        Exception? failure = null;

        void Body()
        {
            try
            {
                for (int i = 0; i < iterations; i++)
                {
                    using McsLock.Disposable handle = mcsLock.Acquire();
                    if (Interlocked.Increment(ref inside) != 1)
                    {
                        throw new InvalidOperationException("mutual exclusion violated");
                    }

                    if (i % 500 == 0) Thread.SpinWait(100_000);
                    Interlocked.Decrement(ref inside);
                    Interlocked.Increment(ref total);
                }
            }
            catch (Exception e)
            {
                Interlocked.CompareExchange(ref failure, e, null);
            }
        }

        Task[] tasks = new Task[threadCount];
        for (int i = 0; i < tasks.Length; i++)
        {
            tasks[i] = Task.Factory.StartNew(Body, TaskCreationOptions.LongRunning);
        }
        Assert.That(Task.WaitAll(tasks, TimeSpan.FromSeconds(60)), Is.True, "stress run deadlocked");

        Assert.That(failure, Is.Null);
        Assert.That(Interlocked.Read(ref total), Is.EqualTo((long)threadCount * iterations));
    }
}
