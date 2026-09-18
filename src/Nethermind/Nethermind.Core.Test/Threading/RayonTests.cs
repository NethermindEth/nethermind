// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core.Threading;
using NUnit.Framework;

namespace Nethermind.Core.Test.Threading;

/// <summary>Semantic tests that hold on both the thread pool and the sequential zkVM implementation.</summary>
[Parallelizable(ParallelScope.All)]
public class RayonTests
{
    [Test]
    public void Join_ReturnsResults_AndPropagatesExceptions([Values] bool fromWorker, [Values] bool throwInB)
    {
        InvalidOperationException expected = new("boom");
        int otherSideRan = 0;

        (int, string) JoinOnce()
        {
            (int, string) result = Rayon.Join(
                () =>
                {
                    if (!throwInB) ThrowFromHelper(expected);
                    Interlocked.Exchange(ref otherSideRan, 1);
                    return 1;
                },
                () =>
                {
                    if (throwInB) ThrowFromHelper(expected);
                    Interlocked.Exchange(ref otherSideRan, 1);
                    return "b";
                });
            return result;
        }

        Func<(int, string)> act = fromWorker ? () => Rayon.Join(JoinOnce, () => 0).Item1 : JoinOnce;

        InvalidOperationException exception = Assert.Catch<InvalidOperationException>(() => act())!;
        Assert.That(exception, Is.SameAs(expected));
        Assert.That(exception.StackTrace, Does.Contain(nameof(ThrowFromHelper)));
        Assert.That(otherSideRan, Is.EqualTo(1), "the non-throwing operand must complete before the join returns");

        Assert.That(Rayon.Join(() => 1, () => "b"), Is.EqualTo((1, "b")));
        Assert.That(Rayon.Join(2, static x => x * 10, "c", static s => s + s), Is.EqualTo((20, "cc")));
    }

    [Test]
    public void Join_Nested_ToDepth([Values(8, 20)] int depth)
    {
        static long Sum(int lo, int hi)
        {
            if (hi - lo <= 1) return lo;
            int mid = (lo + hi) / 2;
            (long left, long right) = Rayon.Join((lo, mid), static r => Sum(r.lo, r.mid), (mid, hi), static r => Sum(r.mid, r.hi));
            return left + right;
        }

        int count = 1 << depth;
        Assert.That(Sum(0, count), Is.EqualTo((long)count * (count - 1) / 2));
    }

    [Test]
    public void For_CoversEveryIndexExactlyOnce([Values(0, 1, 7, 1000, 100_000)] int count)
    {
        int[] hits = new int[count];
        Rayon.For(0, count, hits, static (h, i) => Interlocked.Increment(ref h[i]));
        Assert.That(hits, Is.All.EqualTo(1));

        long sum = 0;
        Rayon.For(0, count, i => Interlocked.Add(ref sum, i));
        Assert.That(sum, Is.EqualTo(Enumerable.Range(0, count).Sum(i => (long)i)));

        if (count > 0)
        {
            InvalidOperationException expected = new("boom");
            InvalidOperationException exception = Assert.Catch<InvalidOperationException>(() => Rayon.For(0, count, i =>
            {
                if (i == count / 2) throw expected;
            }))!;
            Assert.That(exception, Is.SameAs(expected));
        }
    }

    [Test]
    [CancelAfter(60_000)]
    public void Join_ManyConcurrentNonWorkerCallers()
    {
        const int callers = 64;
        long[] results = new long[callers];
        Task[] tasks = new Task[callers];
        for (int c = 0; c < callers; c++)
        {
            int caller = c;
            tasks[c] = Task.Run(() =>
            {
                long total = 0;
                Rayon.For(0, 1000, i => Interlocked.Add(ref total, i));
                (long a, long b) = Rayon.Join(() => Sum(0, 512), () => Sum(512, 1024));
                results[caller] = total + a + b;
            });
        }

        Task.WaitAll(tasks);
        long expected = Enumerable.Range(0, 1000).Sum(i => (long)i) + Enumerable.Range(0, 1024).Sum(i => (long)i);
        Assert.That(results, Is.All.EqualTo(expected));

        static long Sum(int lo, int hi)
        {
            if (hi - lo <= 16) return Enumerable.Range(lo, hi - lo).Sum(i => (long)i);
            int mid = (lo + hi) / 2;
            (long l, long r) = Rayon.Join(() => Sum(lo, mid), () => Sum(mid, hi));
            return l + r;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowFromHelper(Exception exception) => throw exception;
}
