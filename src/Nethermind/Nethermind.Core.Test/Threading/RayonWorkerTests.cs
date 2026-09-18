// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Nethermind.Core.Threading;
using NUnit.Framework;

namespace Nethermind.Core.Test.Threading;

/// <summary>Tests of the thread-pool implementation; not linked into the zkVM test project.</summary>
[Parallelizable(ParallelScope.All)]
public class RayonWorkerTests
{
    [Test]
    public void Join_RunsOnWorkerThreads_AndStolenJobExceptionPropagates()
    {
        Assert.That(Rayon.WorkerCount, Is.EqualTo(Environment.ProcessorCount));
        Assert.That(Rayon.IsWorkerThread, Is.False);

        InvalidOperationException expected = new("boom");
        using ManualResetEventSlim bStarted = new();
        int threadOfA = 0;
        int threadOfB = 0;

        InvalidOperationException exception = Assert.Catch<InvalidOperationException>(() => Rayon.Join(
            () =>
            {
                Assert.That(Rayon.IsWorkerThread, Is.True);
                threadOfA = Environment.CurrentManagedThreadId;
                // Keep a busy until a thief has taken b (or give up and let b run inline).
                bStarted.Wait(TimeSpan.FromSeconds(5));
                return 0;
            },
            int () =>
            {
                threadOfB = Environment.CurrentManagedThreadId;
                bStarted.Set();
                throw expected;
            }))!;

        Assert.That(exception, Is.SameAs(expected));
        Assert.That(threadOfA, Is.Not.Zero);
        if (Rayon.WorkerCount > 1)
        {
            Assert.That(threadOfB, Is.Not.EqualTo(threadOfA), "b should have been stolen while a was blocked");
        }
    }
}
