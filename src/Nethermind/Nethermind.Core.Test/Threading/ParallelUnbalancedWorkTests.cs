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

public partial class ParallelUnbalancedWorkTests
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
    public void BackgroundFor_starts_before_join_and_joiner_executes_remaining_work()
    {
        if (Nethermind.Core.Cpu.RuntimeInformation.IsSingleProcessor) Assert.Ignore("Requires a background worker.");
        using ManualResetEventSlim entered = new();
        using ManualResetEventSlim release = new();
        int caller = Environment.CurrentManagedThreadId;
        int[] threads = new int[2];
        using ParallelUnbalancedWork.BackgroundWork work = ParallelUnbalancedWork.BackgroundFor(0, 2,
            new ParallelOptions { MaxDegreeOfParallelism = 2 }, i =>
            {
                threads[i] = Environment.CurrentManagedThreadId;
                if (i == 0)
                {
                    entered.Set();
                    if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
                }
                else
                {
                    release.Set();
                }
            });
        try
        {
            Assert.That(entered.Wait(TimeSpan.FromSeconds(10)), Is.True, "Work must start before joining.");
            work.WaitForCompletion();
        }
        finally
        {
            release.Set();
        }
        using (Assert.EnterMultipleScope())
        {
            Assert.That(threads[0], Is.Not.EqualTo(caller));
            Assert.That(threads[1], Is.EqualTo(caller), "The joiner must help, rather than only wait.");
        }
    }

    [Test]
    public void BackgroundFor_late_worker_cannot_execute_after_join()
    {
        int calls = 0;
        int finalized = 0;
        using ParallelUnbalancedWork.BackgroundWork work = ParallelUnbalancedWork.BackgroundFor(0, 10,
            new ParallelOptions { MaxDegreeOfParallelism = 1 }, _ => calls++, () => finalized++);
        work.WaitForCompletion();
        if (!Nethermind.Core.Cpu.RuntimeInformation.IsSingleProcessor)
            ((IThreadPoolWorkItem)(object)work).Execute();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(calls, Is.EqualTo(10));
            Assert.That(finalized, Is.EqualTo(1));
        }
    }

    [Test]
    public void BackgroundFor_abandons_deferred_work()
    {
        int count = 0;
        int finalized = 0;
        using ParallelUnbalancedWork.BackgroundWork work = ParallelUnbalancedWork.BackgroundFor(0, 1000,
            new ParallelOptions { MaxDegreeOfParallelism = 1 }, _ => count++, () => finalized++);
        work.Dispose();
        if (!Nethermind.Core.Cpu.RuntimeInformation.IsSingleProcessor)
            ((IThreadPoolWorkItem)(object)work).Execute();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(count, Is.Zero);
            Assert.That(finalized, Is.Zero);
            Assert.Throws<ObjectDisposedException>(work.WaitForCompletion);
        }
    }

    [Test]
    public void BackgroundFor_finalizes_on_active_joiner_or_last_worker([Values] bool sleepingJoiner)
    {
        if (Nethermind.Core.Cpu.RuntimeInformation.IsSingleProcessor) Assert.Ignore("Requires a background worker.");
        using ManualResetEventSlim entered = new();
        using ManualResetEventSlim release = new();
        using ManualResetEventSlim workerDone = new();
        int finalizer = 0;
        using ParallelUnbalancedWork.BackgroundWork work = ParallelUnbalancedWork.BackgroundFor(0, sleepingJoiner ? 1 : 2,
            new ParallelOptions { MaxDegreeOfParallelism = 1 }, i =>
            {
                if (i == 0)
                {
                    entered.Set();
                    if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
                }
                else
                {
                    release.Set();
                    if (!workerDone.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
                }
            }, () => finalizer = Environment.CurrentManagedThreadId);
        Thread worker = new(() => { ((IThreadPoolWorkItem)(object)work).Execute(); workerDone.Set(); });
        Exception? joinError = null;
        Thread joiner = new(() =>
        {
            try { work.WaitForCompletion(); }
            catch (Exception ex) { joinError = ex; }
        });
        worker.Start();
        try
        {
            Assert.That(entered.Wait(TimeSpan.FromSeconds(10)), Is.True);
            joiner.Start();
            if (sleepingJoiner)
            {
                Assert.That(SpinWait.SpinUntil(() => (joiner.ThreadState & ThreadState.WaitSleepJoin) != 0,
                    TimeSpan.FromSeconds(10)), Is.True);
                release.Set();
            }
            Assert.That(joiner.Join(TimeSpan.FromSeconds(10)), Is.True);
        }
        finally
        {
            release.Set();
            worker.Join();
            if (joiner.ThreadState != ThreadState.Unstarted) joiner.Join();
        }
        Assert.That(joinError, Is.Null);
        Assert.That(finalizer, Is.EqualTo(sleepingJoiner ? worker.ManagedThreadId : joiner.ManagedThreadId));
    }

    [Test]
    public void BackgroundFor_disposal_waits_for_running_iteration()
    {
        if (Nethermind.Core.Cpu.RuntimeInformation.IsSingleProcessor) Assert.Ignore("Requires a background worker.");
        using ManualResetEventSlim entered = new();
        using ManualResetEventSlim release = new();
        using ManualResetEventSlim disposing = new();
        using ManualResetEventSlim returned = new();
        int calls = 0;
        int finalized = 0;
        using ParallelUnbalancedWork.BackgroundWork work = ParallelUnbalancedWork.BackgroundFor(0, 100,
            new ParallelOptions { MaxDegreeOfParallelism = 1 }, _ =>
            {
                Interlocked.Increment(ref calls);
                entered.Set();
                if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
            }, () => finalized++);
        Thread worker = new(() => ((IThreadPoolWorkItem)(object)work).Execute());
        Thread disposer = new(() => { disposing.Set(); work.Dispose(); returned.Set(); });
        worker.Start();
        try
        {
            Assert.That(entered.Wait(TimeSpan.FromSeconds(10)), Is.True);
            disposer.Start();
            Assert.That(disposing.Wait(TimeSpan.FromSeconds(10)), Is.True);
            Assert.That(SpinWait.SpinUntil(() => (disposer.ThreadState & ThreadState.WaitSleepJoin) != 0,
                TimeSpan.FromSeconds(10)), Is.True);
            Assert.That(returned.IsSet, Is.False);
        }
        finally
        {
            release.Set();
            worker.Join();
            if (disposer.ThreadState != ThreadState.Unstarted) disposer.Join();
        }
        using (Assert.EnterMultipleScope())
        {
            Assert.That(calls, Is.EqualTo(1));
            Assert.That(finalized, Is.Zero);
        }
    }

    [Test]
    public void BackgroundFor_finalizer_handoff_preserves_all_results([Values] bool scoped)
    {
        using ParallelUnbalancedWork.WorkerScope? scope = scoped ? ParallelUnbalancedWork.BeginWorkerScope(4) : null;
        for (int pass = 0; pass < 1000; pass++)
        {
            int[] results = new int[64];
            int finalized = 0;
            using ParallelUnbalancedWork.BackgroundWork work = ParallelUnbalancedWork.BackgroundFor(0, results.Length,
                FourThreads, i =>
                {
                    Thread.SpinWait(i * 4);
                    results[i] = 1;
                }, () =>
                {
                    Assert.That(results, Is.All.EqualTo(1));
                    finalized++;
                });
            work.WaitForCompletion();
            Assert.That(finalized, Is.EqualTo(1));
        }
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

    [Test]
    public void Continuation_chain_needs_only_the_final_join([Values(1, 4)] int workers)
    {
        int[] values = new int[128];
        ParallelOptions options = new() { MaxDegreeOfParallelism = workers };
        int sum = 0;
        using ParallelUnbalancedWork.BackgroundWork first = ParallelUnbalancedWork.BackgroundFor(0, values.Length,
            options, i => values[i] = i + 1);
        using ParallelUnbalancedWork.BackgroundWork second = first.ContinueWith(0, values.Length, options,
            i => values[i] *= 2);
        using ParallelUnbalancedWork.BackgroundWork last = second.ContinueWith(() => sum = values.Sum());
        last.WaitForCompletion();
        last.WaitForCompletion();
        Assert.That(sum, Is.EqualTo(128 * 129));
    }

    [Test]
    public void Continuation_starts_without_joining([Values] bool attachAfterCompletion, [Values(1, 2)] int count)
    {
        if (Nethermind.Core.Cpu.RuntimeInformation.IsSingleProcessor) Assert.Ignore("Requires background workers.");
        using ManualResetEventSlim release = new(attachAfterCompletion);
        using ManualResetEventSlim continued = new();
        using ParallelUnbalancedWork.BackgroundWork first = ParallelUnbalancedWork.BackgroundFor(0, count, FourThreads,
            _ => { if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException(); });
        try
        {
            if (attachAfterCompletion) first.WaitForCompletion();
            using ParallelUnbalancedWork.BackgroundWork last = first.ContinueWith(continued.Set);
            release.Set();
            Assert.That(continued.Wait(TimeSpan.FromSeconds(10)), Is.True);
            last.WaitForCompletion();
        }
        finally
        {
            release.Set();
        }
    }

    [Test]
    public void Continuation_chain_propagates_faults_and_skips_later_stages([Values(0, 1, 2)] int faultStage)
    {
        InvalidOperationException expected = new("stage failed");
        int[] calls = new int[3];
        void Run(int stage)
        {
            calls[stage]++;
            if (stage == faultStage) throw expected;
        }
        using ParallelUnbalancedWork.BackgroundWork first = ParallelUnbalancedWork.BackgroundFor(0, 1,
            FourThreads, _ => Run(0));
        using ParallelUnbalancedWork.BackgroundWork second = first.ContinueWith(() => Run(1));
        using ParallelUnbalancedWork.BackgroundWork last = second.ContinueWith(() => Run(2));
        Assert.That(Assert.Throws<InvalidOperationException>(last.WaitForCompletion), Is.SameAs(expected));
        Assert.That(calls, Is.EqualTo(Enumerable.Range(0, 3).Select(i => i <= faultStage ? 1 : 0)));
        Assert.DoesNotThrow(last.Dispose);
    }

    [Test]
    public void Joining_serial_continuation_respects_worker_limit()
    {
        if (Nethermind.Core.Cpu.RuntimeInformation.IsSingleProcessor) Assert.Ignore("Requires background workers.");
        using ManualResetEventSlim entered = new();
        using ManualResetEventSlim release = new();
        int calls = 0;
        using ParallelUnbalancedWork.BackgroundWork first = ParallelUnbalancedWork.BackgroundFor(0, 2, FourThreads, _ => { });
        first.WaitForCompletion();
        using ParallelUnbalancedWork.BackgroundWork last = first.ContinueWith(0, 2,
            new ParallelOptions { MaxDegreeOfParallelism = 1 }, _ =>
            {
                Interlocked.Increment(ref calls);
                entered.Set();
                if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
            });
        Exception? joinError = null;
        Thread joiner = new(() =>
        {
            try { last.WaitForCompletion(); }
            catch (Exception ex) { joinError = ex; }
        });
        try
        {
            Assert.That(entered.Wait(TimeSpan.FromSeconds(10)), Is.True);
            joiner.Start();
            Assert.That(SpinWait.SpinUntil(() => (joiner.ThreadState & ThreadState.WaitSleepJoin) != 0,
                TimeSpan.FromSeconds(10)), Is.True);
            Assert.That(Volatile.Read(ref calls), Is.EqualTo(1));
        }
        finally
        {
            release.Set();
            if (joiner.ThreadState != ThreadState.Unstarted) joiner.Join();
        }
        using (Assert.EnterMultipleScope())
        {
            Assert.That(joinError, Is.Null);
            Assert.That(calls, Is.EqualTo(2));
        }
    }

    [Test]
    public void Continuation_chain_cancellation_skips_later_stages([Values(0, 1)] int cancelledStage)
    {
        using CancellationTokenSource source = new();
        ParallelOptions options = new() { MaxDegreeOfParallelism = 1, CancellationToken = source.Token };
        int calls = 0;
        using ParallelUnbalancedWork.BackgroundWork first = ParallelUnbalancedWork.BackgroundFor(0, 1, options,
            _ => { if (cancelledStage == 0) source.Cancel(); });
        using ParallelUnbalancedWork.BackgroundWork second = first.ContinueWith(0, 1, options, _ => source.Cancel());
        using ParallelUnbalancedWork.BackgroundWork last = second.ContinueWith(() => calls++);
        Assert.Throws<OperationCanceledException>(last.WaitForCompletion);
        Assert.That(calls, Is.Zero);
    }

    [Test]
    public void Disposing_final_stage_abandons_the_whole_deferred_chain()
    {
        int calls = 0;
        using ParallelUnbalancedWork.BackgroundWork first = ParallelUnbalancedWork.BackgroundFor(0, 10,
            new ParallelOptions { MaxDegreeOfParallelism = 1 }, _ => calls++);
        using ParallelUnbalancedWork.BackgroundWork last = first.ContinueWith(() => calls++);
        last.Dispose();
        if (!Nethermind.Core.Cpu.RuntimeInformation.IsSingleProcessor)
        {
            ((IThreadPoolWorkItem)(object)first).Execute();
            ((IThreadPoolWorkItem)(object)last).Execute();
        }
        Assert.That(calls, Is.Zero);
        Assert.Throws<ObjectDisposedException>(first.WaitForCompletion);
    }

    [Test]
    public void Disposing_final_stage_drains_running_callbacks([Values] bool inContinuation)
    {
        if (Nethermind.Core.Cpu.RuntimeInformation.IsSingleProcessor) Assert.Ignore("Requires background workers.");
        using ManualResetEventSlim entered = new();
        using ManualResetEventSlim release = new();
        using ParallelUnbalancedWork.BackgroundWork first = ParallelUnbalancedWork.BackgroundFor(0, 2, FourThreads,
            _ => { if (!inContinuation) Block(); });
        using ParallelUnbalancedWork.BackgroundWork last = first.ContinueWith(() => { if (inContinuation) Block(); });
        Thread disposer = new(last.Dispose);
        try
        {
            Assert.That(entered.Wait(TimeSpan.FromSeconds(10)), Is.True);
            disposer.Start();
            Assert.That(SpinWait.SpinUntil(() => (disposer.ThreadState & ThreadState.WaitSleepJoin) != 0,
                TimeSpan.FromSeconds(10)), Is.True);
            Assert.That(disposer.IsAlive, Is.True);
        }
        finally
        {
            release.Set();
            if (disposer.ThreadState != ThreadState.Unstarted) disposer.Join();
        }

        void Block()
        {
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
        }
    }
    [Test]
    public void Worker_scope_reserves_the_caller_and_shares_capacity([Values(2, 4)] int concurrency)
    {
        if (Nethermind.Core.Cpu.RuntimeInformation.IsSingleProcessor) Assert.Ignore("Requires background workers.");
        using ParallelUnbalancedWork.WorkerScope scope = ParallelUnbalancedWork.BeginWorkerScope(concurrency);
        using CountdownEvent entered = new(concurrency - 1);
        using ManualResetEventSlim release = new();
        int caller = Environment.CurrentManagedThreadId;
        using ParallelUnbalancedWork.BackgroundWork background = ParallelUnbalancedWork.BackgroundFor(0, concurrency,
            new ParallelOptions { MaxDegreeOfParallelism = concurrency }, i =>
            {
                if (i == concurrency - 1) return;
                entered.Signal();
                if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
            });
        try
        {
            Assert.That(entered.Wait(TimeSpan.FromSeconds(10)), Is.True);
            int count = 0;
            ParallelUnbalancedWork.For(0, 128, FourThreads, _ =>
            {
                Assert.That(Environment.CurrentManagedThreadId, Is.EqualTo(caller));
                count++;
            });
            Assert.That(count, Is.EqualTo(128));
        }
        finally { release.Set(); }
        background.WaitForCompletion();
    }

    [Test]
    public void Worker_scope_background_range_runs_on_every_slot_of_the_budget([Values(2, 4)] int concurrency)
    {
        if (Nethermind.Core.Cpu.RuntimeInformation.IsSingleProcessor) Assert.Ignore("Requires background workers.");
        using ParallelUnbalancedWork.WorkerScope scope = ParallelUnbalancedWork.BeginWorkerScope(concurrency);
        using CountdownEvent arrived = new(concurrency);
        int timeouts = 0;
        using ParallelUnbalancedWork.BackgroundWork work = ParallelUnbalancedWork.BackgroundFor(0, concurrency,
            new ParallelOptions { MaxDegreeOfParallelism = concurrency }, _ =>
            {
                arrived.Signal();
                if (!arrived.Wait(TimeSpan.FromSeconds(10))) Interlocked.Increment(ref timeouts);
            });
        work.WaitForCompletion();
        Assert.That(timeouts, Is.Zero, "A joiner taking a queued slot retires a requested runner, leaving the budget short.");
    }

    [Test]
    public void Worker_scope_nested_loops_and_continuations_complete([Values(1, 2, 4)] int concurrency)
    {
        using ParallelUnbalancedWork.WorkerScope scope = ParallelUnbalancedWork.BeginWorkerScope(concurrency);
        using ParallelUnbalancedWork.WorkerScope nestedScope = ParallelUnbalancedWork.BeginWorkerScope(concurrency * 2);
        using ThreadLocal<int> depth = new(() => 0);
        int active = 0;
        int[] calls = new int[128];
        int finalized = 0;
        using ParallelUnbalancedWork.BackgroundWork first = ParallelUnbalancedWork.BackgroundFor(0, 128, FourThreads,
            i => Observe(() => Interlocked.Increment(ref calls[i])));
        using ParallelUnbalancedWork.BackgroundWork last = first.ContinueWith(() =>
            ParallelUnbalancedWork.For(0, 128, FourThreads, () => 0,
                (i, local) =>
                {
                    Observe(() => ParallelUnbalancedWork.For(0, 2, FourThreads,
                        _ => Observe(() => Interlocked.Increment(ref calls[i]))));
                    return local + 1;
                }, local => Interlocked.Add(ref finalized, local)));
        ParallelUnbalancedWork.For(0, 128, FourThreads, i => Observe(() => Interlocked.Increment(ref calls[i])));
        last.WaitForCompletion();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(calls, Is.All.EqualTo(4));
            Assert.That(finalized, Is.EqualTo(128));
        }

        void Observe(Action action)
        {
            bool entered = depth.Value++ == 0;
            int running = entered ? Interlocked.Increment(ref active) : 0;
            try
            {
                Assert.That(running, Is.LessThanOrEqualTo(concurrency));
                action();
            }
            finally
            {
                depth.Value--;
                if (entered) Interlocked.Decrement(ref active);
            }
        }
    }

    [Test]
    public void Worker_scope_nested_failure_drains_and_preserves_exception()
    {
        using ParallelUnbalancedWork.WorkerScope scope = ParallelUnbalancedWork.BeginWorkerScope(2);
        InvalidOperationException expected = new("nested failure");
        using ParallelUnbalancedWork.BackgroundWork first = ParallelUnbalancedWork.BackgroundFor(0, 2, FourThreads, _ => { });
        using ParallelUnbalancedWork.BackgroundWork last = first.ContinueWith(() =>
            ParallelUnbalancedWork.For(0, 64, FourThreads, _ => throw expected));
        Assert.That(Assert.Throws<InvalidOperationException>(last.WaitForCompletion), Is.SameAs(expected));
        int count = 0;
        ParallelUnbalancedWork.For(0, 64, FourThreads, _ => Interlocked.Increment(ref count));
        Assert.That(count, Is.EqualTo(64));
    }

    [Test]
    public void Joined_work_releases_callback_state()
    {
        (ParallelUnbalancedWork.BackgroundWork work, WeakReference state) = CreateCompletedWork();
        using (work)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            Assert.That(state.IsAlive, Is.False);
            GC.KeepAlive(work);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (ParallelUnbalancedWork.BackgroundWork, WeakReference) CreateCompletedWork()
    {
        byte[] state = new byte[1024];
        WeakReference reference = new(state);
        ParallelUnbalancedWork.BackgroundWork work = ParallelUnbalancedWork.BackgroundFor(0, 1,
            new ParallelOptions { MaxDegreeOfParallelism = 1 }, _ => state[0]++, () => state[1]++);
        work.WaitForCompletion();
        return (work, reference);
    }

    [Test]
    public void Worker_scope_reuses_busy_worker_between_background_batches()
    {
        if (Nethermind.Core.Cpu.RuntimeInformation.IsSingleProcessor) Assert.Ignore("Requires a background worker.");
        using ParallelUnbalancedWork.WorkerScope scope = ParallelUnbalancedWork.BeginWorkerScope(2);
        using ManualResetEventSlim entered = new();
        using ManualResetEventSlim release = new();
        using ManualResetEventSlim secondRan = new();
        using ParallelUnbalancedWork.BackgroundWork first = ParallelUnbalancedWork.BackgroundFor(0, 128, FourThreads, i =>
        {
            if (i == 0)
            {
                entered.Set();
                if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
            }
            if (i == 64 && !secondRan.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
        });
        try
        {
            Assert.That(entered.Wait(TimeSpan.FromSeconds(10)), Is.True);
            using ParallelUnbalancedWork.BackgroundWork second = ParallelUnbalancedWork.BackgroundFor(0, 1, FourThreads,
                _ => secondRan.Set());
            release.Set();
            Assert.That(secondRan.Wait(TimeSpan.FromSeconds(10)), Is.True, "Ready work must run before the first range finishes.");
            second.WaitForCompletion();
        }
        finally { release.Set(); }
        first.WaitForCompletion();
    }

}
