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
    // 1 forces the unstriped (single-field) path, 8 forces the striped (array) path — both must be correct
    // regardless of the host's core count.
    [TestCase(1)]
    [TestCase(8)]
    public void Starts_at_zero(int slots)
    {
        StripedLongCounter counter = new(slots);
        Assert.That(counter.Value, Is.Zero);
    }

    [TestCase(1)]
    [TestCase(8)]
    public void Accumulates_positive_and_negative_deltas(int slots)
    {
        StripedLongCounter counter = new(slots);
        counter.Add(10);
        counter.Add(5);
        counter.Add(-3);
        Assert.That(counter.Value, Is.EqualTo(12));
    }

    [TestCase(1)]
    [TestCase(8)]
    public void Reset_replaces_the_total(int slots)
    {
        StripedLongCounter counter = new(slots);
        counter.Add(100);
        counter.Reset(42);
        Assert.That(counter.Value, Is.EqualTo(42));

        counter.Add(8);
        Assert.That(counter.Value, Is.EqualTo(50));
    }

    [TestCase(1)]
    [TestCase(8)]
    public void Concurrent_adds_sum_exactly(int slots)
    {
        const int perThread = 100_000;
        int threads = Math.Max(2, Environment.ProcessorCount);
        StripedLongCounter counter = new(slots);

        Parallel.For(0, threads, _ =>
        {
            for (int i = 0; i < perThread; i++)
            {
                counter.Add(1);
            }
        });

        Assert.That(counter.Value, Is.EqualTo((long)threads * perThread));
    }

    [TestCase(1)]
    [TestCase(8)]
    public void Concurrent_increment_and_decrement_net_to_zero(int slots)
    {
        const int perThread = 200_000;
        int pairs = Math.Max(1, Environment.ProcessorCount / 2);
        StripedLongCounter counter = new(slots);

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

    // Striping is skipped at or below the threshold (no array allocated) and capped above it.
    [TestCase(1, 1)]
    [TestCase(4, 1)]
    [TestCase(StripedLongCounter.StripeThreshold, 1)]
    [TestCase(9, 16)]
    [TestCase(16, 16)]
    [TestCase(64, 64)]
    [TestCase(128, StripedLongCounter.MaxSlots)]
    [TestCase(1000, StripedLongCounter.MaxSlots)]
    public void SlotsForProcessorCount_skips_striping_below_threshold_and_caps_above(int cores, int expectedSlots) =>
        Assert.That(StripedLongCounter.SlotsForProcessorCount(cores), Is.EqualTo(expectedSlots));

    [Test]
    public void Unstriped_counter_allocates_less_than_striped()
    {
        // Warm up jit/type init so it doesn't pollute the measurement.
        GC.KeepAlive(new StripedLongCounter(1));
        GC.KeepAlive(new StripedLongCounter(MaxStripeForTest));

        long before = GC.GetAllocatedBytesForCurrentThread();
        StripedLongCounter plain = new(1);
        long plainAlloc = GC.GetAllocatedBytesForCurrentThread() - before;

        before = GC.GetAllocatedBytesForCurrentThread();
        StripedLongCounter striped = new(MaxStripeForTest);
        long stripedAlloc = GC.GetAllocatedBytesForCurrentThread() - before;

        GC.KeepAlive(plain);
        GC.KeepAlive(striped);
        Assert.That(plainAlloc, Is.LessThan(stripedAlloc), "unstriped path must not allocate the per-slot array");
    }

    private const int MaxStripeForTest = 64;

    // Local-only: demonstrates the contention win of the striped path over a single shared Interlocked
    // target. Excluded from CI (timing-dependent; the win scales with core count and only engages above
    // the StripeThreshold). Set STRIPED_BENCH_OUT to capture the result line.
    [Test, Explicit]
    public void Benchmark_striped_vs_shared_under_contention()
    {
        int lanes = Math.Max(2, Environment.ProcessorCount);
        const int perThread = 20_000_000;
        int threads = lanes;

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
