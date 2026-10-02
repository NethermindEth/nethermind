// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using Nethermind.BeaconChain.Threading;
using NUnit.Framework;

namespace Nethermind.BeaconChain.Test.Threading;

public class BeaconParallelTests
{
    /// <summary>
    /// A loop on the import thread whose helpers sit in the thread-pool queue waits as long as that queue, which is minutes
    /// while the pool is starved; the helpers of these loops never reach the pool.
    /// </summary>
    [Test]
    public void Loop_bodies_started_from_a_dedicated_thread_never_run_on_pool_threads()
    {
        Assume.That(Environment.ProcessorCount, Is.GreaterThan(1));
        ConcurrentBag<(int Thread, bool Pool)> bodies = [];
        RunOnDedicatedThread(() => BeaconParallel.For(0, 64, _ =>
        {
            bodies.Add((Environment.CurrentManagedThreadId, Thread.CurrentThread.IsThreadPoolThread));
            Thread.Sleep(1);
        }));

        Assert.Multiple(() =>
        {
            Assert.That(bodies, Has.Count.EqualTo(64));
            Assert.That(bodies.Where(static body => body.Pool), Is.Empty, "a loop body ran on a thread-pool thread");
            Assert.That(bodies.Select(static body => body.Thread).Distinct().Count(), Is.GreaterThan(1), "the loop did not fan out");
        });
    }

    [Test]
    public void Nested_loops_from_concurrent_callers_complete_when_they_hold_every_compute_thread()
    {
        const int callers = 4;
        int outer = Environment.ProcessorCount * 2;
        int total = 0;
        RunOnDedicatedThreads(callers, () => BeaconParallel.For(0, outer, _ =>
            BeaconParallel.For(0, 16, _ =>
            {
                Interlocked.Increment(ref total);
                Thread.Sleep(1);
            })));

        Assert.That(total, Is.EqualTo(callers * outer * 16));
    }

    [Test]
    public void A_fault_in_a_body_reaches_the_caller() =>
        Assert.That(() => BeaconParallel.For(0, 64, static i =>
        {
            if (i == 37) throw new InvalidOperationException("body 37");
        }), Throws.InstanceOf<AggregateException>().With.InnerException.Message.EqualTo("body 37"));

    private static void RunOnDedicatedThread(Action action) => RunOnDedicatedThreads(1, action);

    private static void RunOnDedicatedThreads(int count, Action action)
    {
        ConcurrentQueue<Exception> faults = [];
        Thread[] threads = [.. Enumerable.Range(0, count).Select(_ => new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception e)
            {
                faults.Enqueue(e);
            }
        }))];
        foreach (Thread thread in threads) thread.Start();
        foreach (Thread thread in threads) Assert.That(thread.Join(TimeSpan.FromMinutes(1)), Is.True, "a loop did not finish");
        if (faults.TryDequeue(out Exception? fault)) throw fault;
    }
}
