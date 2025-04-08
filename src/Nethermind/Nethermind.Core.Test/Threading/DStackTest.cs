// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Core.Threading;
using NUnit.Framework;

namespace Nethermind.Core.Test.Threading;

public class DStackTest
{
    [Test]
    public void TestBasicPushAndPop()
    {
        DStack<int> stack = new DStack<int>(0);
        int value = 0;
        stack.TryPop(out value).Should().BeFalse();
        stack.TryDequeue(out value).Should().BeFalse();

        for (int i = 0; i < 10; i++)
        {
            stack.Push(i);
        }

        for (int i = 9; i >= 0; i--)
        {
            stack.TryPop(out value).Should().BeTrue();
            value.Should().Be(i);
        }

        stack.TryPop(out value).Should().BeFalse();
    }

    [Test]
    public void TestBasicPushAndDequeue()
    {
        DStack<int> stack = new DStack<int>(0);
        int value = 0;
        stack.TryPop(out value).Should().BeFalse();
        stack.TryDequeue(out value).Should().BeFalse();

        for (int i = 0; i < 10; i++)
        {
            stack.Push(i);
        }

        for (int i = 0; i < 10; i++)
        {
            stack.TryDequeue(out value).Should().BeTrue();
            value.Should().Be(i);
        }

        stack.TryDequeue(out value).Should().BeFalse();
    }

    [Test]
    public void TestBasicPushAndPopAndDequeue()
    {
        DStack<int> stack = new DStack<int>(0);
        int value = 0;
        stack.TryPop(out value).Should().BeFalse();
        stack.TryDequeue(out value).Should().BeFalse();

        for (int i = 0; i < 10; i++)
        {
            stack.Push(i);
        }

        stack.TryDequeue(out value);
        value.Should().Be(0);
        stack.TryPop(out value);
        value.Should().Be(9);
        stack.TryDequeue(out value);
        value.Should().Be(1);
        stack.TryPop(out value);
        value.Should().Be(8);
        stack.TryDequeue(out value);
        value.Should().Be(2);
        stack.TryPop(out value);
        value.Should().Be(7);
    }

    [Test]
    [Repeat(1)]
    public void TestBasicConcurrentPushAndPopandDequeue()
    {
        DStack<int> stack = new DStack<int>(0);

        int addCounter = 0;
        int totalToAdd = 10000;
        bool[] wasPushed = new bool[totalToAdd];
        bool[] wasRemoved = new bool[totalToAdd];
        ManualResetEventSlim enqueueFinished = new ManualResetEventSlim(false);

        Task[] enqueueTasks = Enumerable.Range(0, 3).Select(_ => Task.Run(() =>
        {
            int newValue = Interlocked.Increment(ref addCounter);
            while (newValue < totalToAdd)
            {
                Interlocked.CompareExchange(ref wasPushed[newValue], true, false).Should().BeFalse();
                stack.Push(newValue);
                newValue = Interlocked.Increment(ref addCounter);
            }
        })).ToArray();

        Task[] popTasks = Enumerable.Range(0, 2).Select(_ => Task.Run(() =>
        {
            while (true)
            {
                if (stack.TryPop(out int value))
                {
                    Interlocked.CompareExchange(ref wasRemoved[value], true, false).Should().BeFalse();
                }
                else
                {
                    if (enqueueFinished.IsSet) return;
                    Thread.Yield();
                }
            }
        })).ToArray();

        Task[] dequeueTasks = Enumerable.Range(0, 2).Select(_ => Task.Run(() =>
        {
            while (!enqueueFinished.IsSet)
            {
                if (stack.TryDequeue(out int value))
                {
                    Interlocked.CompareExchange(ref wasRemoved[value], true, false).Should().BeFalse();
                }
                else
                {
                    Thread.Yield();
                }
            }
        })).ToArray();

        Task.WaitAll(enqueueTasks);
        enqueueFinished.Set();
        Task.WaitAll(popTasks);
        Task.WaitAll(dequeueTasks);

        for (int i = 1; i < totalToAdd; i++)
        {
            wasPushed[i].Should().BeTrue();
            wasRemoved[i].Should().BeTrue();
        }
    }
}
