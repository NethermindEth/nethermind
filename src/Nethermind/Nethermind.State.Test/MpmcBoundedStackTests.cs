// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using NUnit.Framework;
using Nethermind.State.Flat;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Nethermind.State.Flat.Tests
{
using NUnit.Framework;
using Nethermind.State.Flat;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Nethermind.State.Flat.Tests
{
    [TestFixture]
    public class MpmcBoundedStackTests
    {
        [Test]
        public void Capacity_IsCorrectlyInitialized()
        {
            var stack = new MpmcBoundedStack<int>(16);
            Assert.That(stack.Capacity, Is.EqualTo(16));
        }

        [Test]
        public void Constructor_ThrowsOnNonPowerOfTwo()
        {
            Assert.That(() => new MpmcBoundedStack<int>(10), Throws.ArgumentException);
            Assert.That(() => new MpmcBoundedStack<int>(0), Throws.ArgumentException);
            Assert.That(() => new MpmcBoundedStack<int>(-2), Throws.ArgumentException);
        }

        [Test]
        public void TryPush_WhenNotFull_ReturnsTrue()
        {
            var stack = new MpmcBoundedStack<int>(4);
            bool result = stack.TryPush(42);

            Assert.That(result, Is.True);
            Assert.That(stack.Count, Is.EqualTo(1));
        }

        [Test]
        public void TryPush_WhenFull_ReturnsFalse()
        {
            var stack = new MpmcBoundedStack<int>(2);
            stack.TryPush(1);
            stack.TryPush(2);

            // Attempt to push to full stack
            bool result = stack.TryPush(3);

            Assert.That(result, Is.False);
            Assert.That(stack.Count, Is.EqualTo(2));
        }

        [Test]
        public void TryPop_WhenNotEmpty_ReturnsTrueAndItem()
        {
            var stack = new MpmcBoundedStack<int>(4);
            stack.TryPush(100);

            bool result = stack.TryPop(out int item);

            Assert.That(result, Is.True);
            Assert.That(item, Is.EqualTo(100));
            Assert.That(stack.Count, Is.EqualTo(0));
        }

        [Test]
        public void TryPop_WhenEmpty_ReturnsFalse()
        {
            var stack = new MpmcBoundedStack<int>(4);

            bool result = stack.TryPop(out int item);

            Assert.That(result, Is.False);
            Assert.That(item, Is.EqualTo(default(int)));
        }

        [Test]
        public void Lifo_Order_IsMaintained()
        {
            var stack = new MpmcBoundedStack<int>(4);
            stack.TryPush(1);
            stack.TryPush(2);
            stack.TryPush(3);

            stack.TryPop(out int i1);
            stack.TryPop(out int i2);
            stack.TryPop(out int i3);

            Assert.That(i1, Is.EqualTo(3));
            Assert.That(i2, Is.EqualTo(2));
            Assert.That(i3, Is.EqualTo(1));
        }

        // --- Concurrency Tests ---

        [Test]
        public void Concurrent_Push_Pop_Consistency()
        {
            // High load concurrency test
            int capacity = 1024;
            int operationsPerThread = 10000;
            int producerThreads = 4;
            int consumerThreads = 4;

            var stack = new MpmcBoundedStack<int>(capacity);

            // We use a countdown event to start all threads simultaneously
            var startSignal = new ManualResetEventSlim(false);
            var tasks = new List<Task>();

            // Trackers to verify we didn't lose or duplicate data
            long totalPushed = 0;
            long totalPopped = 0;

            // Producers
            for (int i = 0; i < producerThreads; i++)
            {
                tasks.Add(Task.Run(() =>
                {
                    startSignal.Wait();
                    int count = 0;
                    while (count < operationsPerThread)
                    {
                        if (stack.TryPush(1)) // Push dummy value 1
                        {
                            Interlocked.Increment(ref totalPushed);
                            count++;
                        }
                        else
                        {
                            Thread.SpinWait(1); // Back off if full
                        }
                    }
                }));
            }

            // Consumers
            for (int i = 0; i < consumerThreads; i++)
            {
                tasks.Add(Task.Run(() =>
                {
                    startSignal.Wait();
                    int count = 0;
                    while (count < operationsPerThread)
                    {
                        if (stack.TryPop(out int item))
                        {
                            Interlocked.Increment(ref totalPopped);
                            count++;
                        }
                        else
                        {
                            Thread.SpinWait(1); // Back off if empty
                        }
                    }
                }));
            }

            startSignal.Set();
            Task.WaitAll(tasks.ToArray());

            Assert.That(totalPushed, Is.EqualTo(producerThreads * operationsPerThread), "Total pushed count mismatch");
            Assert.That(totalPopped, Is.EqualTo(consumerThreads * operationsPerThread), "Total popped count mismatch");
            Assert.That(stack.Count, Is.EqualTo(0), "Stack should be empty at the end");
        }

        [Test]
        public void Concurrent_RaceToFill_DoesNotExceedCapacity()
        {
            // Ensure that multiple threads racing to fill the last slot don't overfill
            int capacity = 128;
            var stack = new MpmcBoundedStack<int>(capacity);
            var startSignal = new ManualResetEventSlim(false);

            // Create way more producers than capacity
            var tasks = new Task[capacity * 2];
            int successCount = 0;

            for (int i = 0; i < tasks.Length; i++)
            {
                tasks[i] = Task.Run(() =>
                {
                    startSignal.Wait();
                    if (stack.TryPush(1))
                    {
                        Interlocked.Increment(ref successCount);
                    }
                });
            }

            startSignal.Set();
            Task.WaitAll(tasks);

            Assert.That(stack.Count, Is.EqualTo(capacity));
            Assert.That(successCount, Is.EqualTo(capacity));
        }
    }
}
}
