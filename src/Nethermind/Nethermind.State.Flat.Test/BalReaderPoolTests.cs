// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Nethermind.State.Flat.ScopeProvider;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test;

[TestFixture]
[Parallelizable(ParallelScope.All)]
public class BalReaderPoolTests
{
    [Test]
    public void Ctor_rejects_non_positive_concurrency()
    {
        Assert.Throws<ArgumentOutOfRangeException>(static () => new BalReaderPool(0));
        Assert.Throws<ArgumentOutOfRangeException>(static () => new BalReaderPool(-1));
    }

    [Test]
    public void Drain_runs_each_job_exactly_once()
    {
        using BalReaderPool pool = new(4);
        const int jobCount = 1000;
        int[] hits = new int[jobCount];

        pool.Drain(jobCount, workers: 4, j => Interlocked.Increment(ref hits[j]), CancellationToken.None);

        for (int i = 0; i < jobCount; i++)
            Assert.That(hits[i], Is.EqualTo(1), $"job {i}");
    }

    [Test]
    public void Drain_with_zero_jobs_is_noop()
    {
        using BalReaderPool pool = new(4);
        int count = 0;
        pool.Drain(0, workers: 4, _ => Interlocked.Increment(ref count), CancellationToken.None);
        Assert.That(count, Is.Zero);
    }

    [Test]
    public void Drain_clamps_workers_to_max_concurrency()
    {
        using BalReaderPool pool = new(2);
        int[] hits = new int[64];

        pool.Drain(64, workers: 32, j => Interlocked.Increment(ref hits[j]), CancellationToken.None);

        for (int i = 0; i < 64; i++)
            Assert.That(hits[i], Is.EqualTo(1));
    }

    [Test]
    public void Drain_honors_cancellation()
    {
        using BalReaderPool pool = new(2);
        using CancellationTokenSource cts = new();
        int processed = 0;

        Assert.DoesNotThrow(() => pool.Drain(10_000, workers: 2, j =>
        {
            if (Interlocked.Increment(ref processed) > 16) cts.Cancel();
        }, cts.Token));

        // Some jobs ran, but the cursor stopped issuing new work after cancellation —
        // the exact count is timing-dependent, just verify the batch shut down quickly.
        Assert.That(processed, Is.LessThan(10_000));
    }

    [Test]
    public void Drain_propagates_first_exception()
    {
        using BalReaderPool pool = new(2);
        InvalidOperationException expected = new("boom");

        InvalidOperationException? caught = Assert.Throws<InvalidOperationException>(
            () => pool.Drain(100, workers: 2, j =>
            {
                if (j == 7) throw expected;
            }, CancellationToken.None));

        Assert.That(caught, Is.SameAs(expected));
    }

    [Test]
    public void Drain_reuses_threads_across_batches()
    {
        using BalReaderPool pool = new(2);

        for (int round = 0; round < 5; round++)
        {
            int[] hits = new int[200];
            pool.Drain(200, workers: 2, j => Interlocked.Increment(ref hits[j]), CancellationToken.None);
            for (int i = 0; i < 200; i++) Assert.That(hits[i], Is.EqualTo(1), $"round {round} job {i}");
        }
    }

    [Test]
    public void Dispose_after_use_does_not_throw()
    {
        BalReaderPool pool = new(2);
        pool.Drain(10, workers: 2, _ => { }, CancellationToken.None);
        Assert.DoesNotThrow(pool.Dispose);
    }

    [Test]
    public void Drain_after_dispose_throws()
    {
        BalReaderPool pool = new(2);
        pool.Dispose();
        Assert.Throws<ObjectDisposedException>(() => pool.Drain(1, workers: 1, _ => { }, CancellationToken.None));
    }
}
