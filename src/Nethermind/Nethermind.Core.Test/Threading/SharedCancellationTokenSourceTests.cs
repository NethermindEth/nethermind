// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using Nethermind.Core.Threading;
using NUnit.Framework;

namespace Nethermind.Core.Test.Threading;

public class SharedCancellationTokenSourceTests
{
    [Test]
    public void CancelAndDispose_transitions_from_active_to_cancelled()
    {
        SharedCancellationTokenSource shared = new(new CancellationTokenSource());
        CancellationToken token = shared.Token;

        Assert.That(shared.IsCancellationRequested, Is.False);
        Assert.That(token.IsCancellationRequested, Is.False);

        shared.CancelAndDispose();

        Assert.That(shared.IsCancellationRequested, Is.True);
        Assert.That(token.IsCancellationRequested, Is.True);
    }

    [TestCase(1)]
    [TestCase(2)]
    [TestCase(10)]
    [TestCase(50)]
    public void CancelAndDispose_returns_true_exactly_once(int threadCount)
    {
        SharedCancellationTokenSource shared = new(new CancellationTokenSource());
        using ManualResetEventSlim gate = new();
        int trueCount = 0;

        Thread[] threads = new Thread[threadCount];
        for (int i = 0; i < threadCount; i++)
        {
            threads[i] = new Thread(() =>
            {
                gate.Wait();
                if (shared.CancelAndDispose()) Interlocked.Increment(ref trueCount);
            });
            threads[i].Start();
        }

        gate.Set();
        foreach (Thread t in threads) t.Join();

        Assert.That(trueCount, Is.EqualTo(1));
        Assert.That(shared.IsCancellationRequested, Is.True);
    }
}
