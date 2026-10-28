// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.Core.Threading;
using NUnit.Framework;

namespace Nethermind.Core.Test.Threading;

[Parallelizable(ParallelScope.All)]
public class StripedLongTests
{
    [Test]
    public void Sum_is_exact_under_concurrent_mixed_adds()
    {
        StripedLong counter = new();
        const int threads = 8;
        const int iterations = 100_000;

        Parallel.For(0, threads, _ =>
        {
            for (int i = 0; i < iterations; i++)
            {
                counter.Increment();
                counter.Add(3);
                counter.Add(-2);
            }
        });

        Assert.That(counter.Sum, Is.EqualTo((long)threads * iterations * 2));
    }

    [Test]
    public void Negative_totals_are_representable()
    {
        StripedLong counter = new();
        counter.Add(-5);
        counter.Increment();
        Assert.That(counter.Sum, Is.EqualTo(-4));
    }

    [Test]
    public void New_counter_sums_to_zero() => Assert.That(new StripedLong().Sum, Is.Zero);
}
