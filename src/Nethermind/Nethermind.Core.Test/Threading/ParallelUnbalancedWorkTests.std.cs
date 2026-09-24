// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core.Threading;
using NUnit.Framework;

namespace Nethermind.Core.Test.Threading;

public partial class ParallelUnbalancedWorkTests
{
    [Test]
    public void Worker_scope_assisting_restores_the_callers_scope([Values] bool nested, [Values] bool throws)
    {
        using ParallelUnbalancedWork.WorkerScope root = ParallelUnbalancedWork.BeginWorkerScope(1);
        using ParallelUnbalancedWork.WorkerScope? caller = nested ? ParallelUnbalancedWork.BeginWorkerScope(2) : null;
        InvalidOperationException expected = new();
        CallbackWork work = new(() =>
        {
            Assert.That(ParallelUnbalancedWork.WorkerScope.Current, Is.SameAs(root));
            if (throws) throw expected;
        });

        if (throws) Assert.That(Assert.Throws<InvalidOperationException>(() => root.Run(work)), Is.SameAs(expected));
        else root.Run(work);

        Assert.That(ParallelUnbalancedWork.WorkerScope.Current, Is.SameAs(caller ?? root));
    }

    [Test]
    public void Worker_scope_join_does_not_run_other_operations([Values(1, 32, 1024)] int otherOperations, [Values] bool withdraw)
    {
        Queue<IThreadPoolWorkItem> scheduled = new();
        using ParallelUnbalancedWork.WorkerScope scope = new(4, scheduled.Enqueue);
        int unrelatedCalls = 0;
        CallbackWork unrelated = new(() => unrelatedCalls++);
        for (int i = 0; i < otherOperations; i++)
            scope.Enqueue(new ParallelUnbalancedWork.WorkerScope.WorkQueue(), unrelated);
        ParallelUnbalancedWork.WorkerScope.WorkQueue target = new();
        List<int> results = [];
        scope.Enqueue(target, new CallbackWork(() => results.Add(1)));
        scope.Enqueue(target, new CallbackWork(() => results.Add(2)));
        scope.Enqueue(target, new CallbackWork(() => results.Add(3)));
        if (withdraw) Assert.That(scope.Withdraw(target), Is.EqualTo(3));
        while (scope.TryExecute(target)) { }
        using (Assert.EnterMultipleScope())
        {
            Assert.That(results, Is.EqualTo(withdraw ? Array.Empty<int>() : new[] { 1, 2, 3 }), "Withdrawn callbacks must never run.");
            Assert.That(scope.Withdraw(target), Is.Zero);
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
        ParallelUnbalancedWork.WorkerScope.WorkQueue queue = new();
        int calls = 0;
        CallbackWork? work = null;
        work = new(() =>
        {
            if (++calls < 1000) scope.Enqueue(queue, work!, resuming);
        });
        scope.Enqueue(queue, work);
        while (scheduled.TryDequeue(out IThreadPoolWorkItem? runner)) runner.Execute();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(calls, Is.EqualTo(1000));
            Assert.That(requests, Is.EqualTo(resuming ? 1 : 2), "A requested or yielding runner already covers the next callback.");
        }
        scope.Enqueue(queue, new CallbackWork(() => calls++), resuming: true);
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
        scope.Enqueue(new ParallelUnbalancedWork.WorkerScope.WorkQueue(), new CallbackWork(() => calls++), count: count);
        Assert.That(scheduled, Has.Count.EqualTo(Math.Min(count, 7)), "The caller covers one slot; each other queued callback needs a runner.");
        while (scheduled.TryDequeue(out IThreadPoolWorkItem? runner)) runner.Execute();
        Assert.That(calls, Is.EqualTo(count));
    }

    [Test]
    public void Worker_scope_yields_only_to_other_operations([Values] bool otherFirst)
    {
        Queue<IThreadPoolWorkItem> scheduled = new();
        using ParallelUnbalancedWork.WorkerScope scope = new(4, scheduled.Enqueue);
        ParallelUnbalancedWork.WorkerScope.WorkQueue own = new();
        ParallelUnbalancedWork.WorkerScope.WorkQueue other = new();
        CallbackWork work = new(() => { });
        Assert.That(scope.HasOtherReadyWork(own), Is.False);
        if (otherFirst) scope.Enqueue(other, work);
        scope.Enqueue(own, work, count: 2);
        Assert.That(scope.HasOtherReadyWork(own), Is.EqualTo(otherFirst), "Yielding to its own queued slots only requeues the operation.");
        if (!otherFirst) scope.Enqueue(other, work);
        Assert.That(scope.HasOtherReadyWork(own), Is.True);
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
            scope.Enqueue(new ParallelUnbalancedWork.WorkerScope.WorkQueue(), new CallbackWork(() => { }));
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
        ParallelUnbalancedWork.WorkerScope.WorkQueue queue = new();
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
                scope.Enqueue(queue, first);
                Assert.That(entered.Wait(TimeSpan.FromSeconds(10)), Is.True);
                release.Set();
                scope.Enqueue(queue, second);
                Assert.That(completed.Wait(TimeSpan.FromSeconds(10)), Is.True, "Completion must not require a joining caller.");
            }
        }
        finally { release.Set(); }
        Assert.That(timedOut, Is.Zero);
    }

    private sealed class CallbackWork(Action callback) : IThreadPoolWorkItem
    {
        public void Execute() => callback();
    }
}
