// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using NUnit.Framework;

namespace Nethermind.Core.Test;

// Regression: on shutdown, TrieStore.Dispose() can invoke a registered flush after its target database was
// already disposed by Autofac - the barrier registration is a runtime callback, not a graph edge Autofac's
// dispose ordering can see. The database already flushed itself on Dispose (see DbOnTheRocks' FlushOnExit),
// so a disposed flush target should not fail the whole shutdown flush.
[Parallelizable(ParallelScope.Self)]
public class StatePersistenceBarrierTests
{
    [Test]
    public void FlushDeferred_does_not_throw_when_a_flush_target_is_already_disposed()
    {
        StatePersistenceBarrier barrier = new();
        bool laterFlushRan = false;

        barrier.RegisterFlush(() => throw new ObjectDisposedException("SomeDb"));
        barrier.RegisterFlush(() => laterFlushRan = true);

        Assert.That(() => barrier.FlushDeferred(), Throws.Nothing);
        Assert.That(laterFlushRan, Is.True, "a flush registered after a disposed target must still run");
    }
}
