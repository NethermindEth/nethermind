// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Logging;
using NUnit.Framework;

namespace Nethermind.Core.Test;

public class StatePersistenceBarrierTests
{
    [Test]
    public void FlushDeferred_runs_drains_then_flushes()
    {
        StatePersistenceBarrier barrier = new(LimboLogs.Instance);
        int order = 0;
        int drainedAt = 0;
        int flushedAt = 0;
        barrier.RegisterDrain(() => drainedAt = ++order);
        barrier.RegisterFlush(() => flushedAt = ++order);

        barrier.FlushDeferred();

        Assert.That(drainedAt, Is.EqualTo(1));
        Assert.That(flushedAt, Is.EqualTo(2));
    }

    [Test]
    public void FlushDeferred_skips_disposed_registrant_and_still_runs_the_rest()
    {
        // Regression: on shutdown the container can dispose a block-data DB before
        // TrieStore.PersistOnShutdown flushes state through the barrier. The disposed
        // registrant's callback must not abort the teardown — the remaining callbacks
        // (and the caller's own state flush) must still run.
        StatePersistenceBarrier barrier = new(LimboLogs.Instance);
        bool laterFlushRan = false;
        barrier.RegisterFlush(() => throw new ObjectDisposedException("DbOnTheRocks"));
        barrier.RegisterFlush(() => laterFlushRan = true);

        Assert.DoesNotThrow(barrier.FlushDeferred);
        Assert.That(laterFlushRan, Is.True);
    }

    [Test]
    public void FlushDeferred_propagates_other_exceptions()
    {
        // Only the well-understood disposed-registrant case is tolerated — a genuine
        // flush failure must still surface to the caller.
        StatePersistenceBarrier barrier = new(LimboLogs.Instance);
        barrier.RegisterFlush(() => throw new InvalidOperationException("disk on fire"));

        Assert.Throws<InvalidOperationException>(barrier.FlushDeferred);
    }
}
