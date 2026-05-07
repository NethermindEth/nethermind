// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using NUnit.Framework;

namespace Nethermind.Core.Test.BlockAccessLists;

/// <summary>
/// Covers the deferred-rebuild optimisation in <see cref="ReadOnlyAccountChanges"/>:
/// <see cref="ReadOnlyAccountChanges.LoadPreStateStorage"/> only flips a dirty flag, and
/// the parallel sorted arrays are rebuilt once on first read of <c>StorageChanges</c> /
/// <c>ChangedSlots</c>.
/// </summary>
[TestFixture]
public class ReadOnlyAccountChangesTests
{
    [Test]
    public void LoadPreStateStorage_rebuilds_sorted_arrays_on_access()
    {
        ReadOnlyAccountChanges ac = new(TestItem.AddressA,
            [new ReadOnlySlotChanges(3, [new StorageChange(0, 100)])],
            [], [], [], []);

        ac.LoadPreStateStorage(1, 10);
        ac.LoadPreStateStorage(5, 50);

        UInt256[] keys = [.. ac.ChangedSlots];
        Assert.That(keys, Is.EqualTo(new UInt256[] { 1, 3, 5 }));
    }

    [Test]
    public void LoadPreStateStorage_preserves_existing_slot_changes()
    {
        StorageChange original = new(0, 100);
        ReadOnlyAccountChanges ac = new(TestItem.AddressA,
            [new ReadOnlySlotChanges(3, [original])],
            [], [], [], []);

        ac.LoadPreStateStorage(3, 999);

        Assert.That(ac.TryGetSlotChanges(3, out ReadOnlySlotChanges? slot), Is.True);
        Assert.That(slot!.Changes.Length, Is.EqualTo(2));
        // Prestate is prepended; PrestateAwareIndexComparer treats uint.MaxValue as smallest.
        Assert.That(slot.Changes[0].Index, Is.EqualTo(Eip7928Constants.PrestateIndex));
        Assert.That(slot.Changes[1].Index, Is.EqualTo(0u));
    }

    [Test]
    public void StorageChanges_returns_sorted_order_after_prestate_load()
    {
        ReadOnlyAccountChanges ac = new(TestItem.AddressA,
            [new ReadOnlySlotChanges(10, [new StorageChange(0, 1)])],
            [new UInt256(20)],
            [], [], []);

        ac.LoadPreStateStorage(5, 50);
        ac.LoadPreStateStorage(20, 200);
        ac.LoadPreStateStorage(15, 150);

        ReadOnlySlotChanges[] changes = ac.StorageChanges;
        Assert.That(changes.Length, Is.EqualTo(4));
        Assert.That(changes[0].Key, Is.EqualTo(new UInt256(5)));
        Assert.That(changes[1].Key, Is.EqualTo(new UInt256(10)));
        Assert.That(changes[2].Key, Is.EqualTo(new UInt256(15)));
        Assert.That(changes[3].Key, Is.EqualTo(new UInt256(20)));
    }

    [Test]
    public void HasSlotChangesAtIndex_works_after_prestate_load()
    {
        ReadOnlyAccountChanges ac = new(TestItem.AddressA,
            [new ReadOnlySlotChanges(1, [new StorageChange(5, 100)])],
            [], [], [], []);

        ac.LoadPreStateStorage(2, 200);

        Assert.That(ac.HasSlotChangesAtIndex(5u), Is.True);
        Assert.That(ac.HasSlotChangesAtIndex(Eip7928Constants.PrestateIndex), Is.True);
        Assert.That(ac.HasSlotChangesAtIndex(99u), Is.False);
    }

    [Test]
    public void Large_prestate_load_does_not_degrade_quadratically()
    {
        ReadOnlyAccountChanges ac = new(TestItem.AddressA, [], [], [], [], []);

        for (int i = 0; i < 10_000; i++)
        {
            ac.LoadPreStateStorage(new UInt256((ulong)i * 7919), new UInt256((ulong)i));
        }

        Assert.That(ac.StorageChanges.Length, Is.EqualTo(10_000));
        Assert.That(ac.ChangedSlots.Count, Is.EqualTo(10_000));

        for (int i = 1; i < ac.ChangedSlots.Count; i++)
        {
            Assert.That(ac.ChangedSlots[i], Is.GreaterThan(ac.ChangedSlots[i - 1]));
        }
    }
}
