// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Core.Threading;
using NUnit.Framework;

namespace Nethermind.Core.Test.Threading;

public class ParallelUnbalancedWorkTests
{
    private static readonly ParallelOptions FourThreads = new() { MaxDegreeOfParallelism = 4 };

    [Test]
    public void For_HappyPath_RunsAllIterations()
    {
        int sum = 0;
        ParallelUnbalancedWork.For(0, 1000, i => Interlocked.Add(ref sum, i));

        sum.Should().Be(Enumerable.Range(0, 1000).Sum());
    }

    [Test]
    public void For_WhenWorkerThrows_RethrowsOnCallingThread()
    {
        InvalidOperationException expected = new("boom");

        Action act = () => ParallelUnbalancedWork.For(0, 1000, FourThreads, i =>
        {
            if (i == 500) throw expected;
        });

        act.Should().Throw<InvalidOperationException>()
            .Which.Should().BeSameAs(expected);
    }

    [Test]
    public void For_WhenWorkerThrows_PreservesOriginalStackTrace()
    {
        Action act = () => ParallelUnbalancedWork.For(0, 1000, FourThreads, i =>
        {
            if (i == 100) ThrowFromHelper();
        });

        act.Should().Throw<InvalidOperationException>()
            .Which.StackTrace.Should().Contain(nameof(ThrowFromHelper));
    }

    [Test]
    public void For_WhenManyWorkersThrow_RethrowsExactlyOneException()
    {
        // Every iteration throws; only the first captured exception should surface.
        Action act = () => ParallelUnbalancedWork.For(0, 10_000, FourThreads,
            i => throw new InvalidOperationException($"boom-{i}"));

        act.Should().Throw<InvalidOperationException>();
    }

    [Test]
    public void For_WhenWorkerThrows_WaitsForAllThreadsBeforeRethrow()
    {
        int initCount = 0;
        int finallyCount = 0;

        Action act = () => ParallelUnbalancedWork.For<int>(
            0, 5_000, FourThreads,
            init: () => { Interlocked.Increment(ref initCount); return 0; },
            action: (i, _) =>
            {
                if (i == 17) throw new InvalidOperationException();
                return 0;
            },
            @finally: _ => Interlocked.Increment(ref finallyCount));

        act.Should().Throw<InvalidOperationException>();
        finallyCount.Should().Be(initCount);
        initCount.Should().BeGreaterThan(0);
    }

    [Test]
    public void For_WithThreadLocal_WhenInitThrows_RethrowsOnCallingThread()
    {
        Action act = () => ParallelUnbalancedWork.For<int>(
            0, 100, FourThreads,
            init: () => throw new InvalidOperationException("init failed"),
            action: (_, l) => l,
            @finally: _ => { });

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("init failed");
    }

    [Test]
    public void For_WithThreadLocal_WhenInitThrows_FinallyIsNotCalled()
    {
        int finallyCalls = 0;

        Action act = () => ParallelUnbalancedWork.For<object>(
            0, 100, FourThreads,
            init: () => throw new InvalidOperationException("init failed"),
            action: (_, l) => l,
            @finally: _ => Interlocked.Increment(ref finallyCalls));

        act.Should().Throw<InvalidOperationException>().WithMessage("init failed");
        finallyCalls.Should().Be(0);
    }

    [Test]
    public void For_WhenWorkerFaults_OtherWorkersStopFetchingWork()
    {
        int actionCalls = 0;
        const int range = 100_000;

        Action act = () => ParallelUnbalancedWork.For(0, range, FourThreads, i =>
        {
            Interlocked.Increment(ref actionCalls);
            if (i == 0) throw new InvalidOperationException();
        });

        act.Should().Throw<InvalidOperationException>();
        actionCalls.Should().BeLessThan(range / 2);
    }

    [Test]
    public void For_WithThreadLocal_WhenFinallyThrows_RethrowsOnCallingThread()
    {
        Action act = () => ParallelUnbalancedWork.For<int>(
            0, 100, FourThreads,
            init: () => 0,
            action: (_, l) => l,
            @finally: _ => throw new InvalidOperationException("finally failed"));

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("finally failed");
    }

    [Test]
    public void For_WithThreadLocal_HappyPath_RunsAllIterations()
    {
        int total = 0;

        ParallelUnbalancedWork.For<int>(
            0, 1000, FourThreads,
            init: () => 0,
            action: (i, local) => local + i,
            @finally: local => Interlocked.Add(ref total, local));

        total.Should().Be(Enumerable.Range(0, 1000).Sum());
    }

    [Test]
    public void For_DoesNotLeakWorkerExceptionToThreadPool()
    {
        int unhandled = 0;
        UnhandledExceptionEventHandler handler = (_, _) => Interlocked.Increment(ref unhandled);
        AppDomain.CurrentDomain.UnhandledException += handler;
        try
        {
            Action act = () => ParallelUnbalancedWork.For(0, 1000, FourThreads, i =>
            {
                if (i % 11 == 0) throw new InvalidOperationException();
            });

            act.Should().Throw<InvalidOperationException>();
        }
        finally
        {
            AppDomain.CurrentDomain.UnhandledException -= handler;
        }

        unhandled.Should().Be(0);
    }

    [TestCase(0, 0, 16, 1)]
    [TestCase(0, 1, 16, 1)]
    [TestCase(0, 3, 16, 3)]
    [TestCase(2, 5, 16, 3)]
    [TestCase(0, 32, 16, 16)]
    public void Effective_thread_count_is_capped_by_work_items(int fromInclusive, int toExclusive, int maxDegreeOfParallelism, int expected)
    {
        ParallelOptions parallelOptions = new() { MaxDegreeOfParallelism = maxDegreeOfParallelism };

        int actual = ParallelUnbalancedWork.GetEffectiveThreadCount(fromInclusive, toExclusive, parallelOptions);

        Assert.That(actual, Is.EqualTo(expected));
    }

    [Test]
    public void Thread_local_loop_uses_only_needed_workers()
    {
        int initialized = 0;

        ParallelUnbalancedWork.For(
            0,
            3,
            new ParallelOptions { MaxDegreeOfParallelism = 16 },
            () =>
            {
                Interlocked.Increment(ref initialized);
                return 0;
            },
            static (_, state) => state,
            static _ => { });

        Assert.That(initialized, Is.EqualTo(3));
    }

    [Test]
    public void Thread_local_loop_does_not_execute_work_for_empty_range()
    {
        int initialized = 0;
        int executed = 0;

        ParallelUnbalancedWork.For(
            5,
            5,
            new ParallelOptions { MaxDegreeOfParallelism = 16 },
            () =>
            {
                Interlocked.Increment(ref initialized);
                return 0;
            },
            (_, state) =>
            {
                Interlocked.Increment(ref executed);
                return state;
            },
            static _ => { });

        Assert.Multiple(() =>
        {
            Assert.That(initialized, Is.EqualTo(1));
            Assert.That(executed, Is.EqualTo(0));
        });
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowFromHelper() => throw new InvalidOperationException("from helper");
}
