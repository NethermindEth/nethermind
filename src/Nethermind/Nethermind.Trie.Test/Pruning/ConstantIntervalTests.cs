// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Trie.Pruning;
using NUnit.Framework;

namespace Nethermind.Trie.Test.Pruning;

public class ConstantIntervalTests
{
    [Test]
    public void Rejects_an_interval_that_would_never_persist([Values(0L, -1L)] long interval) =>
        Assert.That(() => new ConstantInterval(interval), Throws.InstanceOf<ArgumentOutOfRangeException>());

    [Test]
    public void Persists_every_nth_block()
    {
        ConstantInterval strategy = new(4);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(strategy.ShouldPersist(8), Is.True);
            Assert.That(strategy.ShouldPersist(9), Is.False);
        }
    }
}
