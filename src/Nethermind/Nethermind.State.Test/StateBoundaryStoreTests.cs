// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Test;
using Nethermind.State;
using NUnit.Framework;

namespace Nethermind.Store.Test;

public class StateBoundaryStoreTests
{
    private static StateBoundaryStore CreateStore(TestMemDb stateDb, TestMemDb blockInfosDb, ulong? retentionWindowBlocks = null) =>
        new(stateDb, blockInfosDb, retentionWindowBlocks);

    [Test]
    public void Reads_null_when_keys_absent()
    {
        StateBoundaryStore store = CreateStore(new TestMemDb(), new TestMemDb());
        Assert.That(store.OldestStateBlock, Is.Null);
        Assert.That(store.BestPersistedState, Is.Null);
    }

    [Test]
    public void RetentionWindowBlocks_is_the_configured_value() =>
        Assert.That(CreateStore(new TestMemDb(), new TestMemDb(), retentionWindowBlocks: 128).RetentionWindowBlocks, Is.EqualTo(128UL));

    [Test]
    public void OldestStateBlock_round_trips_through_the_state_db()
    {
        TestMemDb stateDb = new();
        CreateStore(stateDb, new TestMemDb()).OldestStateBlock = 1234UL;
        Assert.That(CreateStore(stateDb, new TestMemDb()).OldestStateBlock, Is.EqualTo(1234UL));
    }

    [TestCase(100UL, 200UL, 200UL, TestName = "Forward write advances the floor")]
    [TestCase(200UL, 100UL, 200UL, TestName = "Backward write is rejected")]
    [TestCase(100UL, 100UL, 100UL, TestName = "Equal write is a no-op")]
    public void OldestStateBlock_set_against_existing_value(ulong initial, ulong attempted, ulong expected)
    {
        TestMemDb stateDb = new();
        StateBoundaryStore store = CreateStore(stateDb, new TestMemDb());
        store.OldestStateBlock = initial;

        store.OldestStateBlock = attempted;

        Assert.That(store.OldestStateBlock, Is.EqualTo(expected));
        Assert.That(CreateStore(stateDb, new TestMemDb()).OldestStateBlock, Is.EqualTo(expected));
    }

    [Test]
    public void OldestStateBlock_allows_null_reset_then_replays_writes()
    {
        TestMemDb stateDb = new();
        StateBoundaryStore store = CreateStore(stateDb, new TestMemDb());
        store.OldestStateBlock = 200UL;

        store.OldestStateBlock = null;
        Assert.That(store.OldestStateBlock, Is.Null);
        Assert.That(CreateStore(stateDb, new TestMemDb()).OldestStateBlock, Is.Null);

        store.OldestStateBlock = 50UL;
        Assert.That(store.OldestStateBlock, Is.EqualTo(50UL), "after reset a smaller floor is acceptable");
    }

    [Test]
    public void BestPersistedState_round_trips_through_the_block_infos_db_and_accepts_backward_writes()
    {
        TestMemDb blockInfosDb = new();
        StateBoundaryStore store = CreateStore(new TestMemDb(), blockInfosDb);
        store.BestPersistedState = 200UL;

        // Unlike the OldestStateBlock floor, the latest pointer may rewind (deep reorg).
        store.BestPersistedState = 100UL;

        Assert.That(store.BestPersistedState, Is.EqualTo(100UL));
        Assert.That(CreateStore(new TestMemDb(), blockInfosDb).BestPersistedState, Is.EqualTo(100UL));
        Assert.That(store.OldestStateBlock, Is.Null, "the two pointers are independent");
    }
}
