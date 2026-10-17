// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Tracing;
using Nethermind.Config;
using Nethermind.Consensus.Processing;
using Nethermind.Core.Eip2930;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Modules;
using Nethermind.Core.Threading;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Core.Test.Threading;

public partial class ParallelUnbalancedWorkTests
{
    private const string BoundedPoolChildArgument = "--nethermind-bounded-parallel-pool";
    private const int BoundedPoolWorkerCount = 4;

    [Test, NonParallelizable]
    public Task Shared_pipeline_completes_on_a_bounded_pool([Values(2, 4)] int budget) =>
        RunBoundedPoolChild($"pipeline-{budget}");

    [Test]
    public async Task Worker_group_bounds_background_and_independent_callers([Values(1, 2, 4)] int budget)
    {
        ParallelUnbalancedWork.WorkerGroup group = new(budget);
        int active = 0;
        int maximum = 0;
        int wrongGroup = 0;
        int calls = 0;
        void Observe(int _)
        {
            if (!ReferenceEquals(ParallelUnbalancedWork.GetCurrentGroup(), group)) Interlocked.Increment(ref wrongGroup);
            int current = Interlocked.Increment(ref active);
            int observed = Volatile.Read(ref maximum);
            while (current > observed)
            {
                int previous = Interlocked.CompareExchange(ref maximum, current, observed);
                if (previous == observed) break;
                observed = previous;
            }
            Thread.SpinWait(20_000);
            Interlocked.Increment(ref calls);
            Interlocked.Decrement(ref active);
        }

        TaskCompletionSource background = new(TaskCreationOptions.RunContinuationsAsynchronously);
        group.Queue(new CallbackWork(() =>
        {
            try
            {
                using ParallelUnbalancedWork.WorkerScope nested = ParallelUnbalancedWork.BeginWorkerScope(64);
                ParallelUnbalancedWork.For(0, 64, Observe);
                background.SetResult();
            }
            catch (Exception exception) { background.SetException(exception); }
        }));
        Task[] callers = new Task[3];
        for (int i = 0; i < callers.Length; i++)
            callers[i] = Task.Run(() =>
            {
                using ParallelUnbalancedWork.WorkerScope entered = group.Enter();
                ParallelUnbalancedWork.For(0, 64, Observe);
            });
        await Task.WhenAll(callers).WaitAsync(TimeSpan.FromSeconds(10));
        await background.Task.WaitAsync(TimeSpan.FromSeconds(10));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(calls, Is.EqualTo(256));
            Assert.That(maximum, Is.InRange(1, budget));
            Assert.That(wrongGroup, Is.Zero);
            Assert.That(ParallelUnbalancedWork.GetCurrentGroup(), Is.Null);
        }
    }

    [Test]
    public void Limited_scope_caps_fanouts_and_preserves_parent_runner([Values(1, 2)] int budget)
    {
        ParallelUnbalancedWork.WorkerGroup group = new(4);
        ParallelOptions options = new() { MaxDegreeOfParallelism = 4 };
        using ManualResetEventSlim entered = new();
        using ParallelUnbalancedWork.WorkerScope parent = group.Enter();
        int active = 0;
        int maximum = 0;
        int calls = 0;
        void Observe(int _)
        {
            Assert.That(ParallelUnbalancedWork.WorkerScheduler.Current!.Concurrency, Is.EqualTo(budget));
            using (group.Enter()) Assert.That(ParallelUnbalancedWork.GetCurrentGroup(), Is.SameAs(group));
            int current = Interlocked.Increment(ref active);
            int observed = Volatile.Read(ref maximum);
            while (current > observed)
            {
                int previous = Interlocked.CompareExchange(ref maximum, current, observed);
                if (previous == observed) break;
                observed = previous;
            }
            Thread.SpinWait(20_000);
            Interlocked.Increment(ref calls);
            Interlocked.Decrement(ref active);
        }

        using ParallelUnbalancedWork.BackgroundWork warming = ParallelUnbalancedWork.BackgroundFor(0, 1,
            options, _ =>
            {
                using ParallelUnbalancedWork.WorkerScope limited = ParallelUnbalancedWork.BeginLimitedWorkerScope(budget);
                entered.Set();
                using ParallelUnbalancedWork.BackgroundWork first = ParallelUnbalancedWork.BackgroundFor(0, 64,
                    options, Observe);
                using ParallelUnbalancedWork.BackgroundWork second = ParallelUnbalancedWork.BackgroundFor(0, 64,
                    options, Observe);
                ParallelUnbalancedWork.For(0, 64, options, Observe);
                first.WaitForCompletion();
                second.WaitForCompletion();
            });
        Assert.That(entered.Wait(TimeSpan.FromSeconds(10)), Is.True, "the coordinator must start on a parent runner");
        warming.WaitForCompletion();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(calls, Is.EqualTo(192));
            Assert.That(maximum, Is.InRange(1, budget));
            Assert.That(ParallelUnbalancedWork.WorkerScope.Current, Is.SameAs(parent));
        }
    }

    [Test]
    public async Task Limited_scope_background_work_restores_context_after_handoff([Values] bool cancel)
    {
        ParallelUnbalancedWork.WorkerGroup group = new(4);
        using CancellationTokenSource cancellation = new();
        ParallelUnbalancedWork.BackgroundWork work;
        int calls = 0;
        using (group.Enter())
        using (ParallelUnbalancedWork.BeginLimitedWorkerScope(1))
        {
            work = ParallelUnbalancedWork.BackgroundFor(0, 64,
                new ParallelOptions { CancellationToken = cancellation.Token }, _ =>
                {
                    Assert.That(ParallelUnbalancedWork.GetCurrentGroup(), Is.SameAs(group));
                    Assert.That(ParallelUnbalancedWork.WorkerScheduler.Current!.Concurrency, Is.EqualTo(1));
                    Interlocked.Increment(ref calls);
                });
        }
        if (cancel) cancellation.Cancel();
        await Task.Run(() =>
        {
            using (work)
            {
                if (cancel) Assert.Throws<OperationCanceledException>(work.WaitForCompletion);
                else work.WaitForCompletion();
            }
            Assert.That(ParallelUnbalancedWork.WorkerScope.Current, Is.Null);
        }).WaitAsync(TimeSpan.FromSeconds(10));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(calls, Is.EqualTo(cancel ? 0 : 64));
            Assert.That(ParallelUnbalancedWork.WorkerScope.Current, Is.Null);
        }
    }

    [Test]
    public async Task Worker_group_restores_context_and_releases_caller_after_failure()
    {
        ParallelUnbalancedWork.WorkerGroup group = new(2);
        using (ParallelUnbalancedWork.WorkerScope outer = ParallelUnbalancedWork.BeginWorkerScope(1))
        {
            Assert.Throws<InvalidOperationException>(() =>
            {
                using ParallelUnbalancedWork.WorkerScope entered = group.Enter();
                throw new InvalidOperationException();
            });
            Assert.That(ParallelUnbalancedWork.WorkerScope.Current, Is.SameAs(outer));
        }
        await Task.Run(() =>
        {
            using ParallelUnbalancedWork.WorkerScope entered = group.Enter();
            Assert.That(ParallelUnbalancedWork.GetCurrentGroup(), Is.SameAs(group));
        }).WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Test]
    public void Worker_scheduler_reusing_a_queue_preserves_claimed_batch_identity([Values(1, 8)] int count)
    {
        const int batches = 2000;
        using ParallelUnbalancedWork.WorkerScope scope = new(4, static _ => { });
        ParallelUnbalancedWork.WorkerScheduler scheduler = scope.Scheduler;
        ParallelUnbalancedWork.WorkerScheduler.WorkQueue queue = new();
        int[] executed = new int[batches];
        int[] withdrawn = new int[batches];
        int stop = 0;
        using CountdownEvent started = new(3);
        using ManualResetEventSlim claimed = new();
        Task[] consumers = new Task[3];
        for (int i = 0; i < consumers.Length; i++)
            consumers[i] = Task.Run(() =>
            {
                started.Signal();
                while (Volatile.Read(ref stop) == 0)
                    if (!scheduler.TryExecute(queue)) Thread.Yield();
            });
        try
        {
            Assert.That(started.Wait(TimeSpan.FromSeconds(10)), Is.True);
            for (int i = 0; i < batches; i++)
            {
                int batch = i;
                scheduler.Enqueue(queue, new CallbackWork(() =>
                {
                    Interlocked.Increment(ref executed[batch]);
                    if (batch == 0) claimed.Set();
                }), count: count);
                if (batch == 0) Assert.That(claimed.Wait(TimeSpan.FromSeconds(10)), Is.True);
                else Thread.Yield();
                withdrawn[batch] = scheduler.Withdraw(queue);
            }
        }
        finally
        {
            Volatile.Write(ref stop, 1);
            Assert.That(Task.WaitAll(consumers, TimeSpan.FromSeconds(10)), Is.True);
        }
        using (Assert.EnterMultipleScope())
        {
            for (int i = 0; i < batches; i++)
                Assert.That(executed[i] + withdrawn[i], Is.EqualTo(count), $"Batch {i} must be claimed or withdrawn exactly once.");
            Assert.That(queue.Work, Is.Null);
            Assert.That(scheduler.HasReadyWork(queue), Is.False);
        }
    }

    [Test]
    public void Worker_scheduler_publication_during_retirement_preserves_the_budget([Values(2, 4)] int budget)
    {
        const int producers = 3;
        const int batches = 1000;
        using ParallelUnbalancedWork.WorkerScope scope = new(budget);
        ParallelUnbalancedWork.WorkerScheduler scheduler = scope.Scheduler;
        using CountdownEvent completed = new(producers * batches);
        int active = 0;
        int budgetExceeded = 0;
        CallbackWork work = new(() =>
        {
            if (Interlocked.Increment(ref active) >= budget) Interlocked.Increment(ref budgetExceeded);
            Thread.SpinWait(10);
            Interlocked.Decrement(ref active);
            completed.Signal();
        });
        Task[] publishers = new Task[producers];
        for (int i = 0; i < publishers.Length; i++)
            publishers[i] = Task.Run(() =>
            {
                for (int j = 0; j < batches; j++)
                {
                    scheduler.Enqueue(new(), work);
                    Thread.Yield();
                }
            });
        Assert.That(Task.WaitAll(publishers, TimeSpan.FromSeconds(10)), Is.True);
        Assert.That(completed.Wait(TimeSpan.FromSeconds(10)), Is.True, "Published work must survive the last runner retiring.");
        Assert.That(budgetExceeded, Is.Zero, "Background runners must leave the caller's slot reserved.");
    }

    [Test]
    public void Worker_scope_assisting_restores_the_callers_scope([Values] bool nested, [Values] bool throws)
    {
        using ParallelUnbalancedWork.WorkerScope root = ParallelUnbalancedWork.BeginWorkerScope(1);
        using ParallelUnbalancedWork.WorkerScope? caller = nested ? ParallelUnbalancedWork.BeginWorkerScope(2) : null;
        InvalidOperationException expected = new();
        CallbackWork work = new(() =>
        {
            Assert.That(ParallelUnbalancedWork.WorkerScheduler.Current, Is.SameAs(root.Scheduler));
            if (throws) throw expected;
        });

        if (throws) Assert.That(Assert.Throws<InvalidOperationException>(() => root.Scheduler.Run(work, new())), Is.SameAs(expected));
        else root.Scheduler.Run(work, new());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(ParallelUnbalancedWork.WorkerScope.Current, Is.SameAs(caller ?? root));
            Assert.That(new ParallelUnbalancedWork.WorkerScheduler.WorkQueue().Parent, Is.Null);
        }
    }

    [Test]
    public void Worker_scope_join_does_not_run_other_operations([Values(1, 32, 1024)] int otherOperations, [Values] bool withdraw)
    {
        Queue<IThreadPoolWorkItem> scheduled = new();
        using ParallelUnbalancedWork.WorkerScope scope = new(4, scheduled.Enqueue);
        int unrelatedCalls = 0;
        CallbackWork unrelated = new(() => unrelatedCalls++);
        for (int i = 0; i < otherOperations; i++)
            scope.Scheduler.Enqueue(new ParallelUnbalancedWork.WorkerScheduler.WorkQueue(), unrelated);
        ParallelUnbalancedWork.WorkerScheduler.WorkQueue target = new();
        List<int> results = [];
        CallbackWork targetWork = new(() => results.Add(results.Count + 1));
        scope.Scheduler.Enqueue(target, targetWork);
        scope.Scheduler.Enqueue(target, targetWork, count: 2);
        if (withdraw) Assert.That(scope.Scheduler.Withdraw(target), Is.EqualTo(3));
        while (scope.Scheduler.TryExecute(target)) { }
        using (Assert.EnterMultipleScope())
        {
            Assert.That(results, Is.EqualTo(withdraw ? Array.Empty<int>() : new[] { 1, 2, 3 }), "Withdrawn callbacks must never run.");
            Assert.That(scope.Scheduler.Withdraw(target), Is.Zero);
            Assert.That(target.Work, Is.Null, "A drained queue must not keep its work item alive.");
            Assert.That(unrelatedCalls, Is.Zero);
        }
        while (scheduled.TryDequeue(out IThreadPoolWorkItem? runner)) runner.Execute();
        Assert.That(unrelatedCalls, Is.EqualTo(otherOperations));
    }

    [Test]
    public void Worker_scope_coalesces_runner_requests([Values] bool resuming)
    {
        Queue<IThreadPoolWorkItem> scheduled = new();
        int requests = 0;
        using ParallelUnbalancedWork.WorkerScope scope = new(8, runner =>
        {
            requests++;
            scheduled.Enqueue(runner);
        });
        ParallelUnbalancedWork.WorkerScheduler.WorkQueue queue = new();
        int calls = 0;
        CallbackWork? work = null;
        work = new(() =>
        {
            if (++calls < 1000) scope.Scheduler.Enqueue(queue, work!, resuming);
        });
        scope.Scheduler.Enqueue(queue, work);
        while (scheduled.TryDequeue(out IThreadPoolWorkItem? runner)) runner.Execute();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(calls, Is.EqualTo(1000));
            Assert.That(requests, Is.EqualTo(resuming ? 1 : 2), "A requested or yielding runner already covers the next callback.");
        }
        scope.Scheduler.Enqueue(queue, new CallbackWork(() => calls++), resuming: true);
        Assert.That(scheduled, Has.Count.EqualTo(1), "The caller must not inherit the retired runner's context.");
        while (scheduled.TryDequeue(out IThreadPoolWorkItem? runner)) runner.Execute();
        Assert.That(calls, Is.EqualTo(1001));
    }

    [Test]
    public void Worker_scope_batch_requests_a_runner_per_callback_within_the_budget([Values(0, 1, 3, 7, 12)] int count)
    {
        Queue<IThreadPoolWorkItem> scheduled = new();
        using ParallelUnbalancedWork.WorkerScope scope = new(8, scheduled.Enqueue);
        int calls = 0;
        scope.Scheduler.Enqueue(new ParallelUnbalancedWork.WorkerScheduler.WorkQueue(), new CallbackWork(() => calls++), count: count);
        Assert.That(scheduled, Has.Count.EqualTo(Math.Min(count, 7)), "The caller covers one slot; each other queued callback needs a runner.");
        while (scheduled.TryDequeue(out IThreadPoolWorkItem? runner)) runner.Execute();
        Assert.That(calls, Is.EqualTo(count));
    }

    [Test]
    public void Worker_scope_yields_only_to_other_operations([Values] bool otherFirst)
    {
        Queue<IThreadPoolWorkItem> scheduled = new();
        using ParallelUnbalancedWork.WorkerScope scope = new(4, scheduled.Enqueue);
        ParallelUnbalancedWork.WorkerScheduler.WorkQueue own = new();
        ParallelUnbalancedWork.WorkerScheduler.WorkQueue other = new();
        CallbackWork work = new(() => { });
        Assert.That(scope.Scheduler.HasOtherReadyWork(own), Is.False);
        if (otherFirst) scope.Scheduler.Enqueue(other, work);
        scope.Scheduler.Enqueue(own, work, count: 2);
        Assert.That(scope.Scheduler.HasOtherReadyWork(own), Is.EqualTo(otherFirst), "Yielding to its own queued slots only requeues the operation.");
        if (!otherFirst) scope.Scheduler.Enqueue(other, work);
        Assert.That(scope.Scheduler.HasOtherReadyWork(own), Is.True);
    }

    [Test]
    public void Background_work_owner_can_run_its_queued_part_before_joining([Values] bool scoped)
    {
        // Runner requests are dropped: nothing but the owner can run the queued part.
        using ParallelUnbalancedWork.WorkerScope? scope = scoped ? new(2, static _ => { }) : null;
        int[] calls = new int[64];
        using ParallelUnbalancedWork.BackgroundWork work = ParallelUnbalancedWork.BackgroundFor(0, calls.Length,
            new ParallelOptions { MaxDegreeOfParallelism = 2 }, i => Interlocked.Increment(ref calls[i]));
        if (scoped)
        {
            Assert.That(work.TryHelp(), Is.True, "An owner waiting on other work must be able to run its own queued part.");
            Assert.That(calls, Is.All.EqualTo(1));
        }
        Assert.That(work.TryHelp(), Is.False, scoped ? "Nothing is left to help." : "Thread-pool callbacks cannot be taken back.");
        work.WaitForCompletion();
        Assert.That(calls, Is.All.EqualTo(1));
    }

    [TestCase(false, 1)]
    [TestCase(false, 2)]
#if DEBUG
    [TestCase(true, 1)]
    [TestCase(true, 2)]
#endif
    public void Background_join_assists_nested_loop_without_running_unrelated_work(bool enqueueAfterWait, int depth)
    {
        using ParallelUnbalancedWork.WorkerScope scope = new(2, static _ => { });
        using ManualResetEventSlim entered = new();
        using ManualResetEventSlim release = new();
        using ManualResetEventSlim nestedEntered = new();
        using ManualResetEventSlim assisted = new();
        Thread joiningThread = Thread.CurrentThread;
        int helperThread = 0;
        int unrelatedCalls = 0;
        bool nestedCompleted = true;
        scope.Scheduler.Enqueue(new(), new CallbackWork(() => unrelatedCalls++));
        using ParallelUnbalancedWork.BackgroundWork work = ParallelUnbalancedWork.BackgroundFor(0, 1,
            new ParallelOptions { MaxDegreeOfParallelism = 2 }, _ =>
            {
                entered.Set();
                if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
                RunNested(depth);
            });
        void RunNested(int remainingDepth)
        {
            if (remainingDepth > 1)
            {
                scope.Scheduler.Run(new CallbackWork(() => RunNested(remainingDepth - 1)), new());
                return;
            }
            ParallelUnbalancedWork.For(0, 2, new ParallelOptions { MaxDegreeOfParallelism = 2 }, _ =>
            {
                if (Environment.CurrentManagedThreadId != joiningThread.ManagedThreadId)
                {
                    nestedEntered.Set();
                    nestedCompleted = assisted.Wait(TimeSpan.FromSeconds(5));
                }
                else
                {
                    helperThread = Environment.CurrentManagedThreadId;
                    assisted.Set();
                }
            });
        }
        Task worker = Task.Run(() => work.TryHelp());
#if DEBUG
        Action? previousHook = ParallelUnbalancedWork.BackgroundWork.BeforeJoinWait;
        if (enqueueAfterWait) ParallelUnbalancedWork.BackgroundWork.BeforeJoinWait = release.Set;
#endif
        try
        {
            Assert.That(entered.Wait(TimeSpan.FromSeconds(10)), Is.True);
            if (!enqueueAfterWait)
            {
                release.Set();
                Assert.That(nestedEntered.Wait(TimeSpan.FromSeconds(10)), Is.True);
            }
            work.WaitForCompletion();
            using (Assert.EnterMultipleScope())
            {
                Assert.That(nestedCompleted, Is.True, "The nested loop must progress before its running iteration times out.");
                Assert.That(helperThread, Is.EqualTo(joiningThread.ManagedThreadId));
                Assert.That(unrelatedCalls, Is.Zero);
                Assert.That(new ParallelUnbalancedWork.WorkerScheduler.WorkQueue().Parent, Is.Null);
            }
        }
        finally
        {
#if DEBUG
            ParallelUnbalancedWork.BackgroundWork.BeforeJoinWait = previousHook;
#endif
            release.Set();
            assisted.Set();
            worker.GetAwaiter().GetResult();
        }
    }

    [Test]
    public void Worker_scope_tracks_ready_descendants_through_drain_and_requeue([Values] bool withdraw)
    {
        using ParallelUnbalancedWork.WorkerScope scope = new(2, static _ => { });
        ParallelUnbalancedWork.WorkerScheduler.WorkQueue parent = new();
        ParallelUnbalancedWork.WorkerScheduler.WorkQueue? child = null;
        ParallelUnbalancedWork.WorkerScheduler.WorkQueue? grandchild = null;
        int calls = 0;
        CallbackWork work = new(() => calls++);
        scope.Scheduler.Run(new CallbackWork(() =>
        {
            child = new();
            scope.Scheduler.Run(new CallbackWork(() => grandchild = new()), child);
        }), parent);
        for (int pass = 0; pass < 2; pass++)
        {
            scope.Scheduler.Enqueue(grandchild!, work, count: 2);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(parent.ReadyDescendants, Is.EqualTo(1));
                Assert.That(child!.ReadyDescendants, Is.EqualTo(1));
                Assert.That(scope.Scheduler.HasReadyWork(parent), Is.True);
            }
            if (withdraw) Assert.That(scope.Scheduler.Withdraw(grandchild!), Is.EqualTo(2));
            else
            {
                Assert.That(scope.Scheduler.TryExecute(parent, includeDescendants: true), Is.True);
                Assert.That(parent.ReadyDescendants, Is.EqualTo(1));
                Assert.That(scope.Scheduler.TryExecute(parent, includeDescendants: true), Is.True);
            }
            using (Assert.EnterMultipleScope())
            {
                Assert.That(parent.ReadyDescendants, Is.Zero);
                Assert.That(child!.ReadyDescendants, Is.Zero);
                Assert.That(scope.Scheduler.HasReadyWork(parent), Is.False);
            }
        }
        Assert.That(calls, Is.EqualTo(withdraw ? 0 : 4));
    }

    [Test]
    public void Worker_scope_does_not_assist_detached_background_descendants()
    {
        using ParallelUnbalancedWork.WorkerScope scope = new(2, static _ => { });
        ParallelUnbalancedWork.WorkerScheduler.WorkQueue parent = new();
        ParallelUnbalancedWork.BackgroundWork? detached = null;
        int calls = 0;
        try
        {
            scope.Scheduler.Run(new CallbackWork(() => detached = ParallelUnbalancedWork.BackgroundFor(0, 1,
                new ParallelOptions { MaxDegreeOfParallelism = 2 }, _ => calls++)), parent);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(scope.Scheduler.HasReadyWork(parent), Is.False);
                Assert.That(scope.Scheduler.TryExecute(parent, includeDescendants: true), Is.False);
                Assert.That(calls, Is.Zero);
            }
            detached!.WaitForCompletion();
            Assert.That(calls, Is.EqualTo(1));
        }
        finally { detached?.Dispose(); }
    }

    [Test]
    public void Worker_scope_submits_runners_outside_the_queue_lock()
    {
        Task? runner = null;
        using ParallelUnbalancedWork.WorkerScope scope = new(2, work =>
        {
            runner = Task.Run(work.Execute);
            Assert.That(runner.Wait(TimeSpan.FromSeconds(10)), Is.True, "The runner must acquire the queue lock before submission returns.");
        });
        try
        {
            scope.Scheduler.Enqueue(new ParallelUnbalancedWork.WorkerScheduler.WorkQueue(), new CallbackWork(() => { }));
        }
        finally { runner?.GetAwaiter().GetResult(); }
    }

    [Test]
    public void Worker_scope_enqueue_racing_runner_retirement_does_not_strand_work()
    {
        using ParallelUnbalancedWork.WorkerScope scope = ParallelUnbalancedWork.BeginWorkerScope(2);
        using ManualResetEventSlim entered = new();
        using ManualResetEventSlim release = new();
        using ManualResetEventSlim completed = new();
        ParallelUnbalancedWork.WorkerScheduler.WorkQueue queue = new();
        int timedOut = 0;
        CallbackWork first = new(() =>
        {
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(10))) Interlocked.Increment(ref timedOut);
        });
        CallbackWork second = new(completed.Set);
        try
        {
            for (int pass = 0; pass < 1000; pass++)
            {
                entered.Reset();
                release.Reset();
                completed.Reset();
                scope.Scheduler.Enqueue(queue, first);
                Assert.That(entered.Wait(TimeSpan.FromSeconds(10)), Is.True);
                release.Set();
                scope.Scheduler.Enqueue(queue, second);
                Assert.That(completed.Wait(TimeSpan.FromSeconds(10)), Is.True, "Completion must not require a joining caller.");
            }
        }
        finally { release.Set(); }
        Assert.That(timedOut, Is.Zero);
    }

    [Test]
    [NonParallelizable]
    public async Task For_returns_without_a_free_pool_thread_once_the_caller_claims_the_range()
    {
        if (Nethermind.Core.Cpu.RuntimeInformation.IsSingleProcessor) Assert.Ignore("Requires queued workers.");
        foreach (string childVariant in new[] { "plain", "local" })
            await RunBoundedPoolChild(childVariant);
    }

    private static async Task RunBoundedPoolChild(string childVariant)
    {
        ProcessStartInfo start = new(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        start.ArgumentList.Add(typeof(ParallelUnbalancedWorkTests).Assembly.Location);
        start.ArgumentList.Add(BoundedPoolChildArgument);
        start.ArgumentList.Add(childVariant);
        if (childVariant.StartsWith("pipeline-", StringComparison.Ordinal))
            start.Environment["DOTNET_PROCESSOR_COUNT"] = childVariant["pipeline-".Length..];
        using Process child = new() { StartInfo = start };
        Assert.That(child.Start(), Is.True);
        Task<string> output = child.StandardOutput.ReadToEndAsync();
        Task<string> error = child.StandardError.ReadToEndAsync();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
        try
        {
            await child.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            child.Kill(entireProcessTree: true);
            await child.WaitForExitAsync();
            Assert.Fail($"The bounded-pool test did not return.\n{await output}\n{await error}");
        }
        Assert.That(child.ExitCode, Is.Zero, $"{childVariant}: {await output}\n{await error}");
    }

    private static Task<int> Main(string[] args)
    {
        if (args is not [BoundedPoolChildArgument, string variant] || variant is not ("plain" or "local" or "pipeline-2" or "pipeline-4"))
            return MicrosoftTestingPlatformEntryPoint.Main(args);

        // Run before the test host starts so all bounded pool slots can be held by confirmed blockers.
        try
        {
            Assert.That(Thread.CurrentThread.IsThreadPoolThread, Is.False);
            ThreadPool.GetMaxThreads(out _, out int completionPortThreads);
            Assert.That(ThreadPool.SetMinThreads(1, 1), Is.True);
            int workers = variant == "pipeline-2" ? 2 : BoundedPoolWorkerCount;
            Assert.That(ThreadPool.SetMaxThreads(workers, completionPortThreads), Is.True);
            Assert.That(ThreadPool.SetMinThreads(workers, 1), Is.True);
            if (variant.StartsWith("pipeline-", StringComparison.Ordinal))
                Task.Run(() => RunSharedPipeline(workers)).WaitAsync(TimeSpan.FromSeconds(25)).GetAwaiter().GetResult();
            else RunWithOccupiedPool(withLocal: variant == "local");
            return Task.FromResult(0);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return Task.FromResult(1);
        }
    }

    private static void RunSharedPipeline(int budget)
    {
        foreach (string outcome in new[] { "success", "cancel", "failure" })
        {
            using ManualResetEventSlim warmed = new();
            using CancellationTokenSource cancellation = new();
            ParallelUnbalancedWork.WorkerGroup group = new(budget);
            IHasAccessList hint = Substitute.For<IHasAccessList>();
            hint.GetAccessList(Arg.Any<Block>(), Arg.Any<IReleaseSpec>()).Returns(_ =>
            {
                Assert.That(ParallelUnbalancedWork.GetCurrentGroup(), Is.SameAs(group));
                warmed.Set();
                return null;
            });
            using IContainer container = new ContainerBuilder()
                .AddModule(new TestNethermindModule(new BlocksConfig { PreWarmStateConcurrency = Math.Min(budget, 2) }))
                .AddSingleton<ISpecProvider>(new TestSingleReleaseSpecProvider(London.Instance))
                .AddSingleton<IHasAccessList>(hint)
                .Build();
            IMainProcessingContext context = container.Resolve<IMainProcessingContext>();
            IWorldState state = context.WorldState;
            BlockHeader parent;
            using (state.BeginScope(IWorldState.PreGenesis))
            {
                state.CreateAccount(TestItem.AddressA, 10_000.Ether);
                state.CreateAccount(TestItem.AddressB, UInt256.One);
                state.InsertCode(TestItem.AddressB, new byte[] { 0x60, 0x00, 0x54, 0x50, 0x00 }, London.Instance);
                state.Set(new StorageCell(TestItem.AddressB, UInt256.Zero), UInt256.One);
                state.Commit(London.Instance);
                state.CommitTree(0);
                parent = Build.A.BlockHeader.WithNumber(0).WithStateRoot(state.StateRoot).TestObject;
            }
            container.Resolve<IBlockTree>().SuggestBlock(Build.A.Block.WithHeader(parent).TestObject, BlockTreeSuggestOptions.None);
            Transaction[] txs = new Transaction[64];
            for (int i = 0; i < txs.Length; i++)
                txs[i] = Build.A.Transaction.WithType(TxType.EIP1559).WithNonce((ulong)i).WithChainId(BlockchainIds.Mainnet)
                    .WithGasLimit(100_000).WithMaxFeePerGas(30.GWei).WithMaxPriorityFeePerGas(1.GWei)
                    .WithTo(TestItem.AddressB).Signed(TestItem.PrivateKeyA).WithSenderAddress(null).TestObject;
            Block block = Build.A.Block.WithParent(parent).WithNumber(1).WithGasLimit(30_000_000)
                .WithBaseFeePerGas(1.GWei).WithTransactions(txs).TestObject;
            block.Header.IsPostMerge = true;
            RecoverSignatures recovery = container.Resolve<RecoverSignatures>();
            using (group.Enter()) recovery.StartRecovery(block.Hash!, block.Transactions, London.Instance);
            context.BranchProcessor.BlockProcessing += (_, _) =>
            {
                Assert.That(warmed.Wait(TimeSpan.FromSeconds(10)), Is.True, "prewarming must start with recovery sharing the bounded pool");
                if (outcome == "cancel")
                {
                    cancellation.Cancel();
                    cancellation.Token.ThrowIfCancellationRequested();
                }
            };
            if (outcome == "failure") context.BlockProcessor.TransactionsExecuted += FailAfterTransactions;
            try
            {
                void Process()
                {
                    using ParallelUnbalancedWork.WorkerScope workers = group.Enter();
                    context.BranchProcessor.Process(parent, [block], ProcessingOptions.NoValidation,
                        NullBlockTracer.Instance, cancellation.Token);
                }
                if (outcome == "cancel") Assert.Throws<OperationCanceledException>(Process);
                else if (outcome == "failure") Assert.Throws<InvalidOperationException>(Process);
                else
                {
                    Process();
                    Assert.That(block.Transactions, Has.All.Property(nameof(Transaction.SenderAddress)).EqualTo(TestItem.AddressA));
                }
            }
            finally
            {
                context.BlockProcessor.TransactionsExecuted -= FailAfterTransactions;
                ISenderRecoveryProgress? progress = recovery.GetInFlight(block.Transactions);
                if (progress is not null) Assert.That(progress.WaitForCompletion(10_000), Is.True);
            }
            Assert.That(ParallelUnbalancedWork.GetCurrentGroup(), Is.Null);
        }

        static void FailAfterTransactions() => throw new InvalidOperationException("Injected execution failure");
    }

    private static void RunWithOccupiedPool(bool withLocal)
    {
        using ManualResetEventSlim release = new();
        using CountdownEvent occupied = new(BoundedPoolWorkerCount);
        using CountdownEvent finished = new(BoundedPoolWorkerCount);

        int[] calls = new int[64];
        int inits = 0;
        try
        {
            for (int i = 0; i < BoundedPoolWorkerCount; i++)
            {
                ThreadPool.UnsafeQueueUserWorkItem(_ =>
                {
                    occupied.Signal();
                    release.Wait();
                    finished.Signal();
                }, 0, preferLocal: false);
            }
            Assert.That(occupied.Wait(TimeSpan.FromSeconds(10)), Is.True, $"All {BoundedPoolWorkerCount} pool workers must enter their blockers before the loop starts.");
            Console.WriteLine("All bounded-pool workers entered.");
            ParallelOptions options = new() { MaxDegreeOfParallelism = BoundedPoolWorkerCount };
            if (withLocal)
            {
                ParallelUnbalancedWork.For(0, calls.Length, options, () =>
                {
                    Interlocked.Increment(ref inits);
                    return 0;
                }, (i, local) =>
                {
                    Interlocked.Increment(ref calls[i]);
                    return local;
                }, static _ => { });
            }
            else
            {
                ParallelUnbalancedWork.For(0, calls.Length, options, i => Interlocked.Increment(ref calls[i]));
            }
        }
        finally
        {
            release.Set();
            Assert.That(finished.Wait(TimeSpan.FromSeconds(10)), Is.True, "Blockers must finish before their gates are disposed.");
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(calls, Is.All.EqualTo(1));
            if (withLocal) Assert.That(inits, Is.EqualTo(1), "Only the caller ran; withdrawn workers must not initialize.");
        }
    }

    [Test]
    public void Kept_runners_take_the_next_fan_out_and_leave_once_released()
    {
        ParallelUnbalancedWork.WorkerGroup group = new(4);
        group.KeepRunners(TimeSpan.FromSeconds(30));
        try
        {
            int calls = RunFanOutWithBackgroundRunner(group);

            // The fan-out is done, and its runners stay with the group for the next one.
            Assert.That(group.ReservedRunners, Is.GreaterThan(0));

            calls += RunFanOutWithBackgroundRunner(group);
            Assert.That(calls, Is.EqualTo(128));
        }
        finally
        {
            group.ReleaseRunners();
        }

        Assert.That(() => group.ReservedRunners == 0, Is.True.After(5_000, 10), "released runners leave");
    }

    [Test]
    public void Kept_runners_leave_when_the_hold_ends()
    {
        ParallelUnbalancedWork.WorkerGroup group = new(4);
        group.KeepRunners(TimeSpan.FromMilliseconds(20));
        RunFanOutWithBackgroundRunner(group);

        Assert.That(() => group.ReservedRunners == 0, Is.True.After(5_000, 10));
    }

    [Test]
    public void Runners_leave_after_the_fan_out_without_a_hold()
    {
        ParallelUnbalancedWork.WorkerGroup group = new(4);
        RunFanOutWithBackgroundRunner(group);

        Assert.That(() => group.ReservedRunners == 0, Is.True.After(5_000, 10));
    }

    private static int RunFanOutWithBackgroundRunner(ParallelUnbalancedWork.WorkerGroup group)
    {
        using ManualResetEventSlim backgroundEntered = new();
        ParallelOptions options = new() { MaxDegreeOfParallelism = group.Concurrency };
        int callerThread = Environment.CurrentManagedThreadId;
        int calls = 0;
        using (group.Enter())
        {
            ParallelUnbalancedWork.For(0, 64, options, _ =>
            {
                if (Environment.CurrentManagedThreadId == callerThread)
                    Assert.That(backgroundEntered.Wait(TimeSpan.FromSeconds(10)), Is.True, "a background runner must participate");
                else
                    backgroundEntered.Set();
                Interlocked.Increment(ref calls);
            });
        }

        Assert.That(backgroundEntered.IsSet, Is.True, "a background runner must participate");
        return calls;
    }

    private sealed class CallbackWork(Action callback) : IThreadPoolWorkItem
    {
        public void Execute() => callback();
    }
}
