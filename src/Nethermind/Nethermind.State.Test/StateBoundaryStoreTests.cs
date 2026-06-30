// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Test;
using Nethermind.State;
using NUnit.Framework;

namespace Nethermind.Store.Test;

public class StateBoundaryStoreTests
{
    [Test]
    public void Reads_null_when_key_absent() =>
        Assert.That(new StateBoundaryStore(new TestMemDb()).OldestStateBlock, Is.Null);

    [Test]
    public void Round_trips_value_through_kv_store()
    {
        TestMemDb kv = new();
        new StateBoundaryStore(kv).OldestStateBlock = 1234UL;
        Assert.That(new StateBoundaryStore(kv).OldestStateBlock, Is.EqualTo(1234UL));
    }

    [TestCase(100UL, 200UL, 200UL, TestName = "Forward write advances the floor")]
    [TestCase(200UL, 100UL, 200UL, TestName = "Backward write is rejected")]
    [TestCase(100UL, 100UL, 100UL, TestName = "Equal write is a no-op")]
    public void Set_against_existing_value(ulong initial, ulong attempted, ulong expected)
    {
        TestMemDb kv = new();
        StateBoundaryStore store = new(kv) { OldestStateBlock = initial };

        store.OldestStateBlock = attempted;

        Assert.That(store.OldestStateBlock, Is.EqualTo(expected));
        Assert.That(new StateBoundaryStore(kv).OldestStateBlock, Is.EqualTo(expected));
    }

    [Test]
    public void Idempotent_set_does_not_rewrite_kv()
    {
        TestMemDb kv = new();
        StateBoundaryStore store = new(kv) { OldestStateBlock = 42UL };
        kv.Clear();

        store.OldestStateBlock = 42UL;

        Assert.That(kv.Count, Is.EqualTo(0));
    }

    [Test]
    public void Allows_null_reset_then_replays_writes()
    {
        TestMemDb kv = new();
        StateBoundaryStore store = new(kv) { OldestStateBlock = 200UL };

        store.OldestStateBlock = null;
        Assert.That(store.OldestStateBlock, Is.Null);
        Assert.That(new StateBoundaryStore(kv).OldestStateBlock, Is.Null);

        store.OldestStateBlock = 50UL;
        Assert.That(store.OldestStateBlock, Is.EqualTo(50UL), "after reset a smaller floor is acceptable");
    }
}
