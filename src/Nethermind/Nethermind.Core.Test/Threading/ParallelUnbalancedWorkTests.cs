// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
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

        Assert.That(sum, Is.EqualTo(Enumerable.Range(0, 1000).Sum()));
    }

    [Test]
    public void For_WhenWorkerThrows_RethrowsOnCallingThread()
    {
        InvalidOperationException expected = new("boom");

        Action act = () => ParallelUnbalancedWork.For(0, 1000, FourThreads, i =>
        {
            if (i == 500) throw expected;
        });

        InvalidOperationException exception = Assert.Catch<InvalidOperationException>(act)!;
        Assert.That(exception, Is.SameAs(expected));
    }

    [Test]
    public void For_WhenWorkerThrows_PreservesOriginalStackTrace()
    {
        Action act = () => ParallelUnbalancedWork.For(0, 1000, FourThreads, i =>
        {
            if (i == 100) ThrowFromHelper();
        });

        InvalidOperationException exception = Assert.Catch<InvalidOperationException>(act)!;
        Assert.That(exception.StackTrace, Does.Contain(nameof(ThrowFromHelper)));
    }

    [Test]
    public void For_WhenManyWorkersThrow_RethrowsExactlyOneException()
    {
        // Every iteration throws — only the first captured exception should surface.
        Action act = () => ParallelUnbalancedWork.For(0, 10_000, FourThreads,
            i => throw new InvalidOperationException($"boom-{i}"));

        Assert.That(act, Throws.TypeOf<InvalidOperationException>());
    }

    [Test]
    public void For_WhenWorkerThrows_WaitsForAllThreadsBeforeRethrow()
    {
        // If the throwing worker raced ahead of the others, MarkThreadCompleted would not yet have been
        // called for them and the calling thread would either rethrow with workers still in flight or
        // hang on the semaphore. Use init/finally to count actual thread arrivals — with the new
        // capture/rethrow path, every thread that ran init must run finally before For returns.
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

        Assert.That(act, Throws.TypeOf<InvalidOperationException>());
        Assert.That(finallyCount, Is.EqualTo(initCount));
        Assert.That(initCount, Is.GreaterThan(0));
    }

    [Test]
    public void For_WithThreadLocal_WhenInitThrows_RethrowsOnCallingThread()
    {
        Action act = () => ParallelUnbalancedWork.For<int>(
            0, 100, FourThreads,
            init: () => throw new InvalidOperationException("init failed"),
            action: (_, l) => l,
            @finally: _ => { });

        Assert.That(act, Throws.TypeOf<InvalidOperationException>().With.Message.EqualTo("init failed"));
    }

    [Test]
    public void For_WithThreadLocal_WhenInitThrows_FinallyIsNotCalled()
    {
        // Matches BCL Parallel.For<TLocal>: localFinally must not run if localInit threw — otherwise
        // a reference-typed TLocal with non-trivial cleanup would NPE on default(TLocal).
        int finallyCalls = 0;

        Action act = () => ParallelUnbalancedWork.For<object>(
            0, 100, FourThreads,
            init: () => throw new InvalidOperationException("init failed"),
            action: (_, l) => l,
            @finally: _ => Interlocked.Increment(ref finallyCalls));

        Assert.That(act, Throws.TypeOf<InvalidOperationException>().With.Message.EqualTo("init failed"));
        Assert.That(finallyCalls, Is.EqualTo(0));
    }

    [Test]
    public void For_WhenWorkerFaults_OtherWorkersStopFetchingWork()
    {
        int actionCalls = 0;
        const int range = 100_000;

        Action act = () => ParallelUnbalancedWork.For(0, range, FourThreads, i =>
        {
            Interlocked.Increment(ref actionCalls);
            Thread.SpinWait(50);
            if (i == 0) throw new InvalidOperationException();
        });

        Assert.That(act, Throws.TypeOf<InvalidOperationException>());
        Assert.That(actionCalls, Is.LessThan(range / 2));
    }

    [Test]
    public void For_WithThreadLocal_WhenFinallyThrows_RethrowsOnCallingThread()
    {
        Action act = () => ParallelUnbalancedWork.For<int>(
            0, 100, FourThreads,
            init: () => 0,
            action: (_, l) => l,
            @finally: _ => throw new InvalidOperationException("finally failed"));

        Assert.That(act, Throws.TypeOf<InvalidOperationException>().With.Message.EqualTo("finally failed"));
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

        Assert.That(total, Is.EqualTo(Enumerable.Range(0, 1000).Sum()));
    }

    [Test]
    public void For_WithEmptyRange_DoesNotRunActionOrInitializeThreadLocalState()
    {
        int actionCalls = 0;
        int initCalls = 0;
        int finallyCalls = 0;

        ParallelUnbalancedWork.For<int>(
            1, 1, FourThreads,
            init: () => Interlocked.Increment(ref initCalls),
            action: (_, local) => { Interlocked.Increment(ref actionCalls); return local; },
            @finally: _ => Interlocked.Increment(ref finallyCalls));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(actionCalls, Is.EqualTo(0));
            Assert.That(initCalls, Is.EqualTo(0));
            Assert.That(finallyCalls, Is.EqualTo(0));
        }
    }

    [Test]
    public void For_WithThreadLocal_DoesNotInitializeWorkersWithoutWork()
    {
        int initCalls = 0;
        int emptyLocals = 0;

        ParallelUnbalancedWork.For<int>(
            0, 2, FourThreads,
            init: () => { Interlocked.Increment(ref initCalls); return 0; },
            action: (_, local) => local + 1,
            @finally: local =>
            {
                if (local == 0) Interlocked.Increment(ref emptyLocals);
            });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(initCalls, Is.InRange(1, 2));
            Assert.That(emptyLocals, Is.EqualTo(0));
        }
    }

    [Test]
    public void For_DoesNotLeakWorkerExceptionToThreadPool()
    {
        // If a worker exception escaped onto a thread-pool thread it would surface via
        // AppDomain.UnhandledException (and crash the process under default settings). Subscribe and
        // assert nothing fires while the calling thread observes the rethrow.
        int unhandled = 0;
        UnhandledExceptionEventHandler handler = (_, _) => Interlocked.Increment(ref unhandled);
        AppDomain.CurrentDomain.UnhandledException += handler;
        try
        {
            Action act = () => ParallelUnbalancedWork.For(0, 1000, FourThreads, i =>
            {
                if (i % 11 == 0) throw new InvalidOperationException();
            });

            Assert.That(act, Throws.TypeOf<InvalidOperationException>());
        }
        finally
        {
            AppDomain.CurrentDomain.UnhandledException -= handler;
        }

        Assert.That(unhandled, Is.EqualTo(0));
    }

    [Test]
    public void BackgroundFor_runs_each_iteration_once_and_finalizes([Values(1, 2, 8)] int workers,
        [Values(0, 1, 65, 1000)] int count)
    {
        int[] calls = new int[count];
        int finalized = 0;
        int caller = Environment.CurrentManagedThreadId;
        bool joining = false;
        using ParallelUnbalancedWork.BackgroundWork work = ParallelUnbalancedWork.BackgroundFor(0, count,
            new ParallelOptions { MaxDegreeOfParallelism = workers }, i =>
            {
                Assert.That(Environment.CurrentManagedThreadId != caller || joining, Is.True);
                Interlocked.Increment(ref calls[i]);
            }, () =>
            {
                Assert.That(calls, Is.All.EqualTo(1));
                Interlocked.Increment(ref finalized);
            });
        joining = true;
        work.WaitForCompletion();
        work.WaitForCompletion();
        Assert.That(finalized, Is.EqualTo(1));
    }

    [Test]
    public void BackgroundFor_drains_without_join()
    {
        int count = 0;
        using (ParallelUnbalancedWork.BackgroundFor(0, 1000, FourThreads, _ => Interlocked.Increment(ref count))) { }
        Assert.That(count, Is.EqualTo(1000));
    }

    [Test]
    public void BackgroundFor_reports_faults([Values(false, true)] bool finalizerFault)
    {
        InvalidOperationException expected = new("background failure");
        using ParallelUnbalancedWork.BackgroundWork work = ParallelUnbalancedWork.BackgroundFor(0, 100, FourThreads,
            _ => { if (!finalizerFault) throw expected; },
            () => { if (finalizerFault) throw expected; });
        Assert.That(Assert.Catch<InvalidOperationException>(work.WaitForCompletion), Is.SameAs(expected));
        Assert.DoesNotThrow(work.Dispose);
    }

    [Test]
    public void BackgroundFor_cancellation_stops_deferred_work([Values(false, true)] bool duringExecution)
    {
        using CancellationTokenSource source = new();
        int calls = 0;
        bool finalized = false;
        using ParallelUnbalancedWork.BackgroundWork work = ParallelUnbalancedWork.BackgroundFor(0, 100,
            new ParallelOptions { MaxDegreeOfParallelism = 1, CancellationToken = source.Token }, _ =>
            {
                calls++;
                source.Cancel();
            }, () => finalized = true);
        if (!duringExecution) source.Cancel();
        Assert.Throws<OperationCanceledException>(work.WaitForCompletion);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(calls, Is.EqualTo(duringExecution ? 1 : 0));
            Assert.That(finalized, Is.False);
        }
    }

    [Test]
    public void BackgroundFor_late_join_waits_for_finalizer()
    {
        if (Nethermind.Core.Cpu.RuntimeInformation.IsSingleProcessor) Assert.Ignore("Requires a background worker.");
        using ManualResetEventSlim entered = new();
        using ManualResetEventSlim release = new();
        using ManualResetEventSlim joining = new();
        using ManualResetEventSlim returned = new();
        using ParallelUnbalancedWork.BackgroundWork work = ParallelUnbalancedWork.BackgroundFor(0, 2,
            new ParallelOptions { MaxDegreeOfParallelism = 2 }, _ => { }, () =>
            {
                entered.Set();
                if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
            });
        Task? waiter = null;
        try
        {
            Assert.That(entered.Wait(TimeSpan.FromSeconds(10)), Is.True);
            waiter = Task.Run(() => { joining.Set(); work.WaitForCompletion(); returned.Set(); });
            Assert.That(joining.Wait(TimeSpan.FromSeconds(10)), Is.True);
            Assert.That(returned.Wait(20), Is.False);
        }
        finally
        {
            release.Set();
            waiter?.GetAwaiter().GetResult();
        }
        work.WaitForCompletion();
    }

    [Test]
    public void BackgroundFor_handles_integer_boundaries([Values(int.MinValue, -5, int.MaxValue - 5)] int start)
    {
        int count = 0;
        using ParallelUnbalancedWork.BackgroundWork work = ParallelUnbalancedWork.BackgroundFor(start, start + 5,
            new ParallelOptions { MaxDegreeOfParallelism = 8 }, i =>
            {
                Assert.That(i, Is.InRange(start, start + 4));
                Interlocked.Increment(ref count);
            });
        work.WaitForCompletion();
        Assert.That(count, Is.EqualTo(5));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowFromHelper() => throw new InvalidOperationException("from helper");
}
