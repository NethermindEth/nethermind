// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only
#nullable enable

using System.Collections.Generic;
using Microsoft.Extensions.ObjectPool;
using Nethermind.Consensus.Processing.ParallelProcessing.BlockStm;
using NUnit.Framework;

namespace Nethermind.Consensus.Test.Processing.ParallelProcessing.BlockStm;

public class ParallelSchedulerTests
{
    // Tracks every set instance returned to the pool so the test can assert no
    // double-returns (the bug-2 invariant).
    private sealed class TrackingHashSetPool : ObjectPool<HashSet<int>>
    {
        private readonly Dictionary<int, int> _returnCounts = [];
        private readonly object _lock = new();
        public int MaxReturnCount { get; private set; }

        public override HashSet<int> Get() => [];

        public override void Return(HashSet<int> obj)
        {
            int hash = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
            lock (_lock)
            {
                _returnCounts.TryGetValue(hash, out int prior);
                int now = prior + 1;
                _returnCounts[hash] = now;
                if (now > MaxReturnCount) MaxReturnCount = now;
            }
        }
    }

    private static ParallelScheduler NewScheduler(int txCount, out TrackingHashSetPool pool)
    {
        pool = new TrackingHashSetPool();
        return new ParallelScheduler(txCount, pool);
    }

    // Drains NextTask until we get a non-empty Executing task or run out of tries.
    private static TxTask FetchExecution(ParallelScheduler scheduler, int maxTries = 16)
    {
        for (int i = 0; i < maxTries; i++)
        {
            TxTask t = scheduler.NextTask();
            if (!t.IsEmpty && !t.Validating) return t;
            if (!t.IsEmpty && t.Validating)
            {
                // Drop the validation task — release activeTasks via FinishValidation(noop).
                scheduler.FinishValidation(t.TxVersion.TxIndex, aborted: false);
            }
        }
        return TxTask.Empty;
    }

    // Bug 2 regression: previously AbortExecution (race-detect path) and FinishExecution
    // could both observe the same dependency set and both return it to the pool.
    [Test]
    public void Pool_returned_at_most_once_per_finish()
    {
        using ParallelScheduler scheduler = NewScheduler(2, out TrackingHashSetPool pool);

        TxTask tx0 = FetchExecution(scheduler);
        TxTask tx1 = FetchExecution(scheduler);
        Assert.That(tx0.TxVersion.TxIndex, Is.EqualTo(0));
        Assert.That(tx1.TxVersion.TxIndex, Is.EqualTo(1));

        // Park tx 1 on tx 0 (tx 0 still Executing).
        Assert.That(scheduler.AbortExecution(tx1.TxVersion.TxIndex, 0), Is.True);

        // Tx 0 finishes; drains the dep-set and returns it exactly once.
        scheduler.FinishExecution(tx0.TxVersion, writeSetChanged: false);

        Assert.That(pool.MaxReturnCount, Is.LessThanOrEqualTo(1));
    }

    [Test]
    public void Abort_returns_false_when_blocker_already_executed()
    {
        using ParallelScheduler scheduler = NewScheduler(2, out _);

        TxTask tx0 = FetchExecution(scheduler);
        TxTask tx1 = FetchExecution(scheduler);

        // Tx 0 finishes before tx 1 attempts to park on it.
        scheduler.FinishExecution(tx0.TxVersion, writeSetChanged: false);

        Assert.That(scheduler.AbortExecution(tx1.TxVersion.TxIndex, 0), Is.False,
            "blocker already Executed → AbortExecution must short-circuit so caller re-executes");
    }

    // Bug 3 regression: SetReady's torn (Status, Incarnation) write would have let the
    // next claim happen at the OLD incarnation. The packed-CAS fix bumps both fields
    // atomically; the resumed task's incarnation must be 1.
    [Test]
    public void Resumed_task_has_bumped_incarnation()
    {
        using ParallelScheduler scheduler = NewScheduler(2, out _);

        TxTask tx0 = FetchExecution(scheduler);
        TxTask tx1 = FetchExecution(scheduler);

        scheduler.AbortExecution(tx1.TxVersion.TxIndex, 0);
        scheduler.FinishExecution(tx0.TxVersion, writeSetChanged: false);

        TxTask resumed = FetchExecution(scheduler);
        Assert.That(resumed.TxVersion.TxIndex, Is.EqualTo(1));
        Assert.That(resumed.TxVersion.Incarnation, Is.EqualTo(1));
    }

    [Test]
    public void Single_tx_block_completes_after_FinishExecution()
    {
        using ParallelScheduler scheduler = NewScheduler(1, out _);

        TxTask tx0 = FetchExecution(scheduler);
        Assert.That(tx0.TxVersion.TxIndex, Is.EqualTo(0));

        scheduler.FinishExecution(tx0.TxVersion, writeSetChanged: false);

        // After finish, the validation pass will fire once; once that's done, scheduler
        // is Done.
        for (int i = 0; i < 4 && !scheduler.Done; i++)
        {
            TxTask t = scheduler.NextTask();
            if (t.Validating) scheduler.FinishValidation(t.TxVersion.TxIndex, aborted: false);
        }

        Assert.That(scheduler.Done, Is.True);
    }
}
