// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using Nethermind.Core.Test;
using Nethermind.State;
using NUnit.Framework;

namespace Nethermind.Store.Test;

public class StateBoundaryStoreTests
{
    [Test]
    public void Reads_null_when_key_absent() =>
        new StateBoundaryStore(new TestMemDb()).OldestStateBlock.Should().BeNull();

    [Test]
    public void Round_trips_value_through_kv_store()
    {
        TestMemDb kv = new();
        new StateBoundaryStore(kv).OldestStateBlock = 1234;
        new StateBoundaryStore(kv).OldestStateBlock.Should().Be(1234);
    }

    [TestCase(100L, 200L, 200L, TestName = "Forward write advances the floor")]
    [TestCase(200L, 100L, 200L, TestName = "Backward write is rejected")]
    [TestCase(100L, 100L, 100L, TestName = "Equal write is a no-op")]
    public void Set_against_existing_value(long initial, long attempted, long expected)
    {
        TestMemDb kv = new();
        StateBoundaryStore store = new(kv) { OldestStateBlock = initial };

        store.OldestStateBlock = attempted;

        store.OldestStateBlock.Should().Be(expected);
        new StateBoundaryStore(kv).OldestStateBlock.Should().Be(expected);
    }

    [Test]
    public void Idempotent_set_does_not_rewrite_kv()
    {
        TestMemDb kv = new();
        StateBoundaryStore store = new(kv) { OldestStateBlock = 42 };
        kv.Clear();

        store.OldestStateBlock = 42;

        kv.Count.Should().Be(0);
    }

    [Test]
    public void Allows_null_reset_then_replays_writes()
    {
        TestMemDb kv = new();
        StateBoundaryStore store = new(kv) { OldestStateBlock = 200 };

        store.OldestStateBlock = null;
        store.OldestStateBlock.Should().BeNull();
        new StateBoundaryStore(kv).OldestStateBlock.Should().BeNull();

        store.OldestStateBlock = 50;
        store.OldestStateBlock.Should().Be(50, "after reset a smaller floor is acceptable");
    }
}
