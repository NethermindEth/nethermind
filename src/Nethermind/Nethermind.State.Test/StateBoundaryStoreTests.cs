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
    public void Reads_null_when_key_absent()
    {
        TestMemDb kv = new();
        StateBoundaryStore store = new(kv);
        store.OldestStateBlock.Should().BeNull();
    }

    [Test]
    public void Round_trips_value_through_kv_store()
    {
        TestMemDb kv = new();
        new StateBoundaryStore(kv).OldestStateBlock = 1234;
        new StateBoundaryStore(kv).OldestStateBlock.Should().Be(1234);
    }

    [Test]
    public void Idempotent_set_does_not_rewrite_kv()
    {
        TestMemDb kv = new();
        StateBoundaryStore store = new(kv) { OldestStateBlock = 42 };
        kv.Clear();

        store.OldestStateBlock = 42;

        kv.Count.Should().Be(0, "setting the same value should not re-encode and write");
    }

    [Test]
    public void Allows_monotonic_increase()
    {
        TestMemDb kv = new();
        StateBoundaryStore store = new(kv) { OldestStateBlock = 100 };

        store.OldestStateBlock = 200;

        store.OldestStateBlock.Should().Be(200);
        new StateBoundaryStore(kv).OldestStateBlock.Should().Be(200);
    }

    [Test]
    public void Rejects_backward_write()
    {
        TestMemDb kv = new();
        StateBoundaryStore store = new(kv) { OldestStateBlock = 200 };

        store.OldestStateBlock = 100;

        store.OldestStateBlock.Should().Be(200);
        new StateBoundaryStore(kv).OldestStateBlock.Should().Be(200);
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
