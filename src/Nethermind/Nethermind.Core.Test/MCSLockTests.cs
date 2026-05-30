// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Threading;
using NUnit.Framework;

using System.Collections.Generic;
using System.Linq;
using System.Threading;

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
}
