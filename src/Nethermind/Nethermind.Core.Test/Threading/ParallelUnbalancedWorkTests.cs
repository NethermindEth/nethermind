// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core.Threading;
using NUnit.Framework;

namespace Nethermind.Core.Test.Threading;

public class ParallelUnbalancedWorkTests
{
    [TestCase(0, 0, 16, 1)]
    [TestCase(0, 1, 16, 1)]
    [TestCase(0, 3, 16, 3)]
    [TestCase(2, 5, 16, 3)]
    [TestCase(0, 32, 16, 16)]
    public void Effective_thread_count_is_capped_by_work_items(int fromInclusive, int toExclusive, int maxDegreeOfParallelism, int expected)
    {
        ParallelOptions parallelOptions = new() { MaxDegreeOfParallelism = maxDegreeOfParallelism };

        int actual = ParallelUnbalancedWork.GetEffectiveThreadCount(fromInclusive, toExclusive, parallelOptions);

        Assert.That(actual, Is.EqualTo(expected));
    }

    [Test]
    public void Thread_local_loop_uses_only_needed_workers()
    {
        int initialized = 0;

        ParallelUnbalancedWork.For(
            0,
            3,
            new ParallelOptions { MaxDegreeOfParallelism = 16 },
            () =>
            {
                Interlocked.Increment(ref initialized);
                return 0;
            },
            static (_, state) => state,
            static _ => { });

        Assert.That(initialized, Is.EqualTo(3));
    }
}
