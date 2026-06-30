// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core.Threading;
using NUnit.Framework;

namespace Nethermind.Core.Test.Threading;

[Parallelizable(ParallelScope.None)]
public class StripedLongCounterTests
{
    [Test]
    public void Starts_at_zero()
    {
        StripedLongCounter counter = new();
        Assert.That(counter.Value, Is.Zero);
    }

    [Test]
    public void Accumulates_positive_and_negative_deltas()
    {
        StripedLongCounter counter = new();
        counter.Add(10);
        counter.Add(5);
        counter.Add(-3);
        Assert.That(counter.Value, Is.EqualTo(12));
    }

    [Test]
    public void Reset_replaces_the_total()
    {
        StripedLongCounter counter = new();
        counter.Add(100);
        counter.Reset(42);
        Assert.That(counter.Value, Is.EqualTo(42));

        counter.Add(8);
        Assert.That(counter.Value, Is.EqualTo(50));
    }

    [Test]
    public void Concurrent_adds_sum_exactly()
    {
        const int perThread = 100_000;
        int threads = Math.Max(2, Environment.ProcessorCount);
        StripedLongCounter counter = new();

        Parallel.For(0, threads, _ =>
        {
            for (int i = 0; i < perThread; i++)
            {
                counter.Add(1);
            }
        });

        Assert.That(counter.Value, Is.EqualTo((long)threads * perThread));
    }

    [Test]
    public void Concurrent_increment_and_decrement_net_to_zero()
    {
        const int perThread = 200_000;
        int pairs = Math.Max(1, Environment.ProcessorCount / 2);
        StripedLongCounter counter = new();

        Parallel.For(0, pairs * 2, t =>
        {
            long delta = t % 2 == 0 ? 1 : -1;
            for (int i = 0; i < perThread; i++)
            {
                counter.Add(delta);
            }
        });

        Assert.That(counter.Value, Is.Zero);
    }

    // Local-only: demonstrates the contention win over a single shared Interlocked target.
    // Excluded from CI (timing-dependent); run with --filter to reproduce the speedup number.
    [Test, Explicit]
    public void Benchmark_striped_vs_shared_under_contention()
    {
        const int perThread = 20_000_000;
        int threads = Math.Max(2, Environment.ProcessorCount);

        long shared = 0;
        double sharedMs = Measure(threads, () =>
        {
            for (int i = 0; i < perThread; i++)
            {
                Interlocked.Add(ref shared, 1);
            }
        });

        StripedLongCounter striped = new();
        double stripedMs = Measure(threads, () =>
        {
            for (int i = 0; i < perThread; i++)
            {
                striped.Add(1);
            }
        });

        long expected = (long)threads * perThread;
        Assert.That(shared, Is.EqualTo(expected));
        Assert.That(striped.Value, Is.EqualTo(expected));

        string line =
            $"STRIPED_BENCH threads={threads} shared={sharedMs:0.#}ms striped={stripedMs:0.#}ms speedup={sharedMs / stripedMs:0.##}x";
        TestContext.Progress.WriteLine(line);
        string? outPath = Environment.GetEnvironmentVariable("STRIPED_BENCH_OUT");
        if (outPath is not null)
        {
            System.IO.File.AppendAllText(outPath, line + Environment.NewLine);
        }
    }

    private static double Measure(int threads, Action work)
    {
        Thread[] workers = new Thread[threads];
        using Barrier barrier = new(threads + 1);
        for (int t = 0; t < threads; t++)
        {
            workers[t] = new Thread(() =>
            {
                barrier.SignalAndWait();
                work();
                barrier.SignalAndWait();
            });
            workers[t].Start();
        }

        barrier.SignalAndWait();
        long start = Stopwatch.GetTimestamp();
        barrier.SignalAndWait();
        double ms = Stopwatch.GetElapsedTime(start).TotalMilliseconds;

        foreach (Thread worker in workers)
        {
            worker.Join();
        }

        return ms;
    }
}
