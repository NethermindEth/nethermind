// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Threading;
using NUnit.Framework;
using NUnit.Framework.Internal;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace Nethermind.Core.Test;

[TestFixture]
public class McsPriorityLockTests
{
    private McsPriorityLock mcsLock;

    [SetUp]
    public void Setup()
    {
        mcsLock = new McsPriorityLock();
    }

    [Test]
    public void SingleThreadAcquireRelease()
    {
        using (var handle = mcsLock.Acquire())
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
        var threads = new List<Thread>();

        for (int i = 0; i < numberOfThreads; i++)
        {
            var thread = new Thread(() =>
            {
                using var handle = mcsLock.Acquire();

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
        int numberOfThreads = 10;
        var executionOrder = new List<int>();
        var threads = new List<Thread>();

        for (int i = 0; i < numberOfThreads; i++)
        {
            int threadId = i;
            var thread = new Thread(() =>
            {
                using var handle = mcsLock.Acquire();
                executionOrder.Add(threadId);
                Thread.Sleep(15); // Ensure the order is maintained
            });
            threads.Add(thread);
            thread.Start();
            Thread.Sleep(1); // Ensure the order is maintained
        }

        foreach (Thread thread in threads)
        {
            thread.Join();
        }

        var expectedOrder = Enumerable.Range(0, numberOfThreads).ToList();
        CollectionAssert.AreEqual(expectedOrder, executionOrder, "Threads did not acquire lock in the order they were started.");
    }


    [Test]
    public void PriorityQueueJumpingTest()
    {
        int numberOfThreads = 100;
        var threads = new List<Thread>();
        List<int> executionOrder = new();
        Dictionary<Thread, ThreadPriority> threadPriorities = new();

        // Create threads with varying priorities.
        for (int i = 0; i < numberOfThreads; i++)
        {
            ThreadPriority priority = i % 2 == 0 ? ThreadPriority.Highest : ThreadPriority.Normal; // Alternate priorities
            var thread = new Thread(() =>
            {
                using var handle = mcsLock.Acquire();
                executionOrder.Add(Thread.CurrentThread.ManagedThreadId);
                Thread.Sleep(25); // Simulate work
            });
            thread.Priority = priority; // Set thread priority
            threads.Add(thread);
            threadPriorities[thread] = priority;
        }

        // Start threads.
        foreach (var thread in threads)
        {
            thread.Start();
        }

        // Wait for all threads to complete.
        foreach (var thread in threads)
        {
            thread.Join();
        }

        // Analyze execution order based on priority.
        int lowPriorityFirst = 0;
        for (int i = 0; i < executionOrder.Count - 1; i++)
        {
            int currentThreadId = executionOrder[i];
            int nextThreadId = executionOrder[i + 1];
            Thread currentThread = threads.First(t => t.ManagedThreadId == currentThreadId);
            Thread nextThread = threads.First(t => t.ManagedThreadId == nextThreadId);

            if (threadPriorities[currentThread] < threadPriorities[nextThread])
            {
                lowPriorityFirst++;
            }
        }

        // Some lower priority threads will acquire first; we are asserting that they mostly queue jump
        Assert.That(lowPriorityFirst < (numberOfThreads / 8), Is.True, "High priority threads did not acquire the lock before lower priority ones.");
    }

    [Test]
    public void NonReentrantTest()
    {
        bool reentrancyDetected = false;
        var thread = new Thread(() =>
        {
            using var handle = mcsLock.Acquire();
            try
            {
                using var innerHandle = mcsLock.Acquire(); // Attempt to re-lock
            }
            catch
            {
                reentrancyDetected = true;
            }
        });

        thread.Start();
        thread.Join();

        Assert.IsTrue(reentrancyDetected, "Reentrancy was not properly detected.");
    }
}
