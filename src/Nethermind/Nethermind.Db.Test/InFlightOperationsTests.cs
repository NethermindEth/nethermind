// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Db.Rocks;
using NUnit.Framework;

namespace Nethermind.Db.Test;

public class InFlightOperationsTests
{
    private static readonly TimeSpan Long = TimeSpan.FromSeconds(30);

    [Test]
    public void WaitForAll_blocks_while_an_operation_is_pending_and_returns_when_it_ends()
    {
        InFlightOperations operations = new();
        Assert.That(operations.TryBegin(), Is.True);

        operations.Close();
        Task<bool> wait = Task.Run(() => operations.WaitForAll(Long));

        Assert.That(wait.Wait(TimeSpan.FromMilliseconds(200)), Is.False, "the wait must not end while the operation is pending");

        operations.End();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(wait.Wait(Long), Is.True);
            Assert.That(wait.Result, Is.True);
            Assert.That(operations.Count, Is.Zero);
        }
    }

    [Test]
    public void WaitForAll_reports_an_operation_that_outlives_the_timeout()
    {
        InFlightOperations operations = new();
        operations.TryBegin();
        operations.Close();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(operations.WaitForAll(TimeSpan.FromMilliseconds(50)), Is.False);
            Assert.That(operations.Count, Is.EqualTo(1));
        }
    }

    [Test]
    public void TryBegin_after_close_is_refused_and_not_counted()
    {
        InFlightOperations operations = new();
        operations.Close();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(operations.TryBegin(), Is.False);
            Assert.That(operations.Count, Is.Zero);
            Assert.That(operations.WaitForAll(TimeSpan.Zero), Is.True);
        }
    }

    // Workers that keep starting operations while the owner closes: each one is either refused or waited for, so none
    // is still running once the wait returns, and none starts afterwards.
    [Test]
    [Repeat(5)]
    public void Close_racing_starts_leaves_no_operation_running()
    {
        InFlightOperations operations = new();
        int running = 0;
        int startedAfterWait = 0;
        bool waited = false;
        Task[] workers = new Task[4];
        using CountdownEvent started = new(workers.Length);
        for (int w = 0; w < workers.Length; w++)
        {
            workers[w] = Task.Run(() =>
            {
                bool signalled = false;
                while (operations.TryBegin())
                {
                    if (Volatile.Read(ref waited)) Interlocked.Increment(ref startedAfterWait);
                    Interlocked.Increment(ref running);
                    if (!signalled)
                    {
                        signalled = true;
                        started.Signal();
                    }
                    Thread.SpinWait(50);
                    Interlocked.Decrement(ref running);
                    operations.End();
                }
            });
        }

        // Every worker has an operation running, so the close below races real starts and ends.
        bool allWorkersStarted = started.Wait(Long);
        operations.Close();
        bool drained = operations.WaitForAll(Long);
        int runningAfterWait = Volatile.Read(ref running);
        Volatile.Write(ref waited, true);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(allWorkersStarted, Is.True);
            Assert.That(drained, Is.True);
            Assert.That(runningAfterWait, Is.Zero);
            Assert.That(Task.WaitAll(workers, Long), Is.True);
            Assert.That(startedAfterWait, Is.Zero);
        }
    }
}
