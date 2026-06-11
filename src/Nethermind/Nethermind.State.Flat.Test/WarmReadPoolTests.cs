// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Nethermind.State.Flat.ScopeProvider;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test;

[TestFixture]
[Parallelizable(ParallelScope.All)]
public class WarmReadPoolTests
{
    [TestCase(0)]
    [TestCase(-1)]
    public void Ctor_rejects_non_positive_concurrency(int concurrency) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new WarmReadPool(concurrency));

    // pool capacity, requested workers, jobs
    [TestCase(4, 4, 1000)]   // basic
    [TestCase(2, 32, 64)]    // workers clamped down to pool capacity
    [TestCase(1, 1, 200)]    // single-thread pool
    [TestCase(8, 1, 50)]     // single requested worker on a wider pool
    public void Run_executes_each_job_exactly_once(int poolSize, int workers, int jobs)
    {
        using WarmReadPool pool = new(poolSize);
        int[] hits = new int[jobs];

        pool.Run(jobs, workers, j => Interlocked.Increment(ref hits[j]), CancellationToken.None);

        for (int i = 0; i < jobs; i++) Assert.That(hits[i], Is.EqualTo(1), $"job {i}");
    }

    [Test]
    public void Run_with_zero_jobs_is_noop()
    {
        using WarmReadPool pool = new(4);
        int count = 0;
        pool.Run(0, workers: 4, _ => Interlocked.Increment(ref count), CancellationToken.None);
        Assert.That(count, Is.Zero);
    }

    [Test]
    public void Run_reuses_threads_across_batches()
    {
        using WarmReadPool pool = new(2);

        for (int round = 0; round < 5; round++)
        {
            int[] hits = new int[200];
            pool.Run(200, workers: 2, j => Interlocked.Increment(ref hits[j]), CancellationToken.None);
            for (int i = 0; i < 200; i++) Assert.That(hits[i], Is.EqualTo(1), $"round {round} job {i}");
        }
    }

    [Test]
    public void Run_honors_cancellation()
    {
        using WarmReadPool pool = new(2);
        using CancellationTokenSource cts = new();
        int processed = 0;

        Assert.DoesNotThrow(() => pool.Run(10_000, workers: 2, j =>
        {
            if (Interlocked.Increment(ref processed) > 16) cts.Cancel();
        }, cts.Token));

        Assert.That(processed, Is.LessThan(10_000));
    }

    [Test]
    public void Run_propagates_first_exception()
    {
        using WarmReadPool pool = new(2);
        InvalidOperationException expected = new("boom");

        InvalidOperationException? caught = Assert.Throws<InvalidOperationException>(
            () => pool.Run(100, workers: 2, j =>
            {
                if (j == 7) throw expected;
            }, CancellationToken.None));

        Assert.That(caught, Is.SameAs(expected));
    }

    [Test]
    public void Dispose_after_use_does_not_throw()
    {
        WarmReadPool pool = new(2);
        pool.Run(10, workers: 2, _ => { }, CancellationToken.None);
        Assert.DoesNotThrow(pool.Dispose);
    }

    [Test]
    public void Run_after_dispose_throws()
    {
        WarmReadPool pool = new(2);
        pool.Dispose();
        Assert.Throws<ObjectDisposedException>(() => pool.Run(1, workers: 1, _ => { }, CancellationToken.None));
    }

    [Test]
    public void Concurrent_dispose_is_idempotent()
    {
        WarmReadPool pool = new(2);
        pool.Run(10, workers: 2, _ => { }, CancellationToken.None);

        Exception?[] errors = new Exception?[4];
        Thread[] threads = new Thread[errors.Length];
        for (int i = 0; i < threads.Length; i++)
        {
            int idx = i;
            threads[i] = new Thread(() =>
            {
                try { pool.Dispose(); }
                catch (Exception ex) { errors[idx] = ex; }
            });
        }
        foreach (Thread t in threads) t.Start();
        foreach (Thread t in threads) t.Join();

        Assert.That(errors, Is.All.Null);
    }

    [Test]
    public void Dispose_racing_in_flight_run_does_not_crash()
    {
        // Regression: Dispose released shutdown permits while a Run was still draining; an idle
        // worker woke into the stale batch and double-signaled its depleted CountdownEvent,
        // throwing on a background thread and killing the process.
        for (int round = 0; round < 100; round++)
        {
            WarmReadPool pool = new(4);
            using ManualResetEventSlim started = new();
            Exception? runError = null;

            Thread runner = new(() =>
            {
                try
                {
                    pool.Run(10_000, workers: 2, _ =>
                    {
                        started.Set();
                        Thread.SpinWait(50);
                    }, CancellationToken.None);
                }
                catch (ObjectDisposedException) { }
                catch (Exception ex) { runError = ex; }
            });
            runner.Start();
            started.Wait();
            pool.Dispose();
            runner.Join();

            Assert.That(runError, Is.Null, $"round {round}");
        }
    }
}
