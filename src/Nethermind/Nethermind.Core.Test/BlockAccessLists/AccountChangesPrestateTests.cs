// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using NUnit.Framework;
using System;

namespace Nethermind.Core.Test.BlockAccessLists;

/// <summary>
/// Regression tests for the prestate-sentinel sort-order fix introduced when BlockAccessIndex
/// widened from int to uint per EIP-7928 (commit 645099785a). Before
/// <see cref="PrestateAwareIndexComparer"/> the new <c>uint.MaxValue</c> sentinel sorted last,
/// breaking the iteration patterns in <see cref="AccountChanges"/> that expected prestate first.
/// </summary>
[TestFixture]
public class AccountChangesPrestateTests
{
    [Test]
    public void GetBalance_at_index_zero_returns_prestate_when_only_prestate_present()
    {
        AccountChanges ac = new(TestItem.AddressA);
        ac.AddBalanceChange(new BalanceChange(Eip7928Constants.PrestateIndex, 100));

        Assert.That(ac.GetBalance(0), Is.EqualTo((UInt256)100));
    }

    [Test]
    public void GetBalance_returns_latest_change_strictly_before_index()
    {
        AccountChanges ac = new(TestItem.AddressA);
        ac.AddBalanceChange(new BalanceChange(Eip7928Constants.PrestateIndex, 50));
        ac.AddBalanceChange(new BalanceChange(0u, 200));
        ac.AddBalanceChange(new BalanceChange(2u, 300));

        Assert.That(ac.GetBalance(0), Is.EqualTo((UInt256)50), "queried before any tx index — returns prestate");
        Assert.That(ac.GetBalance(1), Is.EqualTo((UInt256)200), "queried at 1 — last change before is 0");
        Assert.That(ac.GetBalance(2), Is.EqualTo((UInt256)200), "queried at 2 — change at 2 is excluded by >=");
        Assert.That(ac.GetBalance(3), Is.EqualTo((UInt256)300), "queried at 3 — last change before is 2");
    }

    [Test]
    public void GetNonce_at_index_zero_returns_prestate_when_only_prestate_present()
    {
        AccountChanges ac = new(TestItem.AddressA);
        ac.AddNonceChange(new NonceChange(Eip7928Constants.PrestateIndex, 7));

        Assert.That(ac.GetNonce(0), Is.EqualTo((UInt256)7));
    }

    [Test]
    public void GetCode_throws_clear_error_when_prestate_code_is_missing()
    {
        AccountChanges ac = new(TestItem.AddressA);

        InvalidOperationException? exception = Assert.Throws<InvalidOperationException>(() => ac.GetCode(0));

        Assert.That(exception!.Message, Does.Contain("prior code change or prestate journal entry"));
    }

    [Test]
    public void GetCodeHash_throws_clear_error_when_prestate_code_is_missing()
    {
        AccountChanges ac = new(TestItem.AddressA);

        InvalidOperationException? exception = Assert.Throws<InvalidOperationException>(() => ac.GetCodeHash(0));

        Assert.That(exception!.Message, Does.Contain("prior code change or prestate journal entry"));
    }

    [Test]
    public void Last_balance_change_ignores_prestate_when_post_state_change_recorded()
    {
        // Pattern used by BlockAccessListManager.ApplyStateChanges — writes state only when
        // [^1].Index != PrestateIndex. Verifies prestate doesn't poison the [^1] check.
        AccountChanges ac = new(TestItem.AddressA);
        ac.AddBalanceChange(new BalanceChange(Eip7928Constants.PrestateIndex, 100));
        ac.AddBalanceChange(new BalanceChange(0u, 200));

        Assert.That(ac.BalanceChanges[^1].Index, Is.Not.EqualTo(Eip7928Constants.PrestateIndex));
        Assert.That(ac.BalanceChanges[^1].Index, Is.EqualTo(0u));
        Assert.That(ac.BalanceChanges[^1].Value, Is.EqualTo((UInt256)200));
    }

    [Test]
    public void Last_balance_change_is_prestate_when_only_prestate_recorded()
    {
        AccountChanges ac = new(TestItem.AddressA);
        ac.AddBalanceChange(new BalanceChange(Eip7928Constants.PrestateIndex, 100));

        Assert.That(ac.BalanceChanges[^1].Index, Is.EqualTo(Eip7928Constants.PrestateIndex));
    }

    [Test]
    public void Prestate_entries_are_enumerated_before_changes_when_grafted_last()
    {
        AccountChanges ac = new(TestItem.AddressA);
        ac.AddBalanceChange(new BalanceChange(0u, 200));
        ac.AddNonceChange(new NonceChange(0u, 1));
        ac.AddCodeChange(new CodeChange(0u, [0x60]));
        ac.GetOrAddSlotChanges(3u).AddStorageChange(new StorageChange(0u, 0x22));

        ac.AddBalanceChange(new BalanceChange(Eip7928Constants.PrestateIndex, 100));
        ac.AddNonceChange(new NonceChange(Eip7928Constants.PrestateIndex, 0));
        ac.AddCodeChange(new CodeChange(Eip7928Constants.PrestateIndex, []));
        ac.GetOrAddSlotChanges(3u).AddStorageChange(new StorageChange(Eip7928Constants.PrestateIndex, 0x11));

        Assert.That(ac.BalanceChanges[0].Index, Is.EqualTo(Eip7928Constants.PrestateIndex));
        Assert.That(ac.BalanceChanges[^1].Index, Is.EqualTo(0u));
        Assert.That(ac.NonceChanges[0].Index, Is.EqualTo(Eip7928Constants.PrestateIndex));
        Assert.That(ac.NonceChanges[^1].Index, Is.EqualTo(0u));
        Assert.That(ac.CodeChanges[0].Index, Is.EqualTo(Eip7928Constants.PrestateIndex));
        Assert.That(ac.CodeChanges[^1].Index, Is.EqualTo(0u));

        SlotChanges slot = ac.GetOrAddSlotChanges(3u);
        Assert.That(slot.Changes.Keys[0], Is.EqualTo(Eip7928Constants.PrestateIndex));
        Assert.That(slot.Changes.Keys[^1], Is.EqualTo(0u));
    }

    [Test]
    public void AccountExists_returns_true_when_change_at_lower_index()
    {
        // AccountExists iterates ascending and breaks when change.Key >= blockAccessIndex.
        // With prestate sorted first, changes at index 0 are reached before the break.
        AccountChanges ac = new(TestItem.AddressA);
        ac.AddNonceChange(new NonceChange(Eip7928Constants.PrestateIndex, 0));
        ac.AddNonceChange(new NonceChange(0u, 1));

        Assert.That(ac.AccountExists(1), Is.True);
    }

    [Test]
    public void Account_state_reads_ignore_future_indices_but_fall_back_to_prestate()
    {
        AccountChanges ac = new(TestItem.AddressA);
        ac.AddBalanceChange(new BalanceChange(Eip7928Constants.PrestateIndex, 50));
        ac.AddNonceChange(new NonceChange(Eip7928Constants.PrestateIndex, 7));
        ac.AddCodeChange(new CodeChange(Eip7928Constants.PrestateIndex, [0x60]));
        ac.AddBalanceChange(new BalanceChange(3u, 100));
        ac.AddNonceChange(new NonceChange(3u, 8));
        ac.AddCodeChange(new CodeChange(3u, [0x61]));

        Assert.That(ac.GetBalance(3), Is.EqualTo((UInt256)50));
        Assert.That(ac.GetNonce(3), Is.EqualTo((UInt256)7));
        Assert.That(ac.GetCode(3), Is.EqualTo(new byte[] { 0x60 }));
        Assert.That(ac.AccountExists(3), Is.False);
        Assert.That(ac.AccountExists(4), Is.True);
    }

    [Test]
    public void Merge_appends_block_access_indices_and_preserves_grafted_prestate_first()
    {
        AccountChanges left = new(TestItem.AddressA);
        left.AddBalanceChange(new BalanceChange(0u, 10));

        AccountChanges right = new(TestItem.AddressA);
        right.AddBalanceChange(new BalanceChange(1u, 20));
        right.AddNonceChange(new NonceChange(1u, 1));
        right.GetOrAddSlotChanges(4u).AddStorageChange(new StorageChange(1u, 0xAA));

        left.Merge(right);
        left.AddBalanceChange(new BalanceChange(Eip7928Constants.PrestateIndex, 5));
        left.AddNonceChange(new NonceChange(Eip7928Constants.PrestateIndex, 0));
        left.GetOrAddSlotChanges(4u).AddStorageChange(new StorageChange(Eip7928Constants.PrestateIndex, 0));

        Assert.That(left.BalanceChanges[0].Index, Is.EqualTo(Eip7928Constants.PrestateIndex));
        Assert.That(left.BalanceChanges[1].Index, Is.EqualTo(0u));
        Assert.That(left.BalanceChanges[2].Index, Is.EqualTo(1u));
        Assert.That(left.NonceChanges[0].Index, Is.EqualTo(Eip7928Constants.PrestateIndex));
        Assert.That(left.NonceChanges[^1].Index, Is.EqualTo(1u));

        SlotChanges slot = left.GetOrAddSlotChanges(4u);
        Assert.That(slot.Changes.Keys[0], Is.EqualTo(Eip7928Constants.PrestateIndex));
        Assert.That(slot.Changes.Keys[^1], Is.EqualTo(1u));
    }

    [Test]
    public void Storage_changes_enumerate_sorted_after_unsorted_writes()
    {
        AccountChanges ac = new(TestItem.AddressA);
        ac.GetOrAddSlotChanges(9u).AddStorageChange(new StorageChange(0u, 0x99));
        ac.GetOrAddSlotChanges(1u).AddStorageChange(new StorageChange(0u, 0x11));

        Assert.That(ac.StorageChanges[0].Key, Is.EqualTo((UInt256)1u));
        Assert.That(ac.StorageChanges[1].Key, Is.EqualTo((UInt256)9u));
        Assert.That(ac.ChangedSlots[0], Is.EqualTo((UInt256)1u));
        Assert.That(ac.ChangedSlots[1], Is.EqualTo((UInt256)9u));
    }

    [Test]
    public void Storage_changes_sorted_cache_invalidates_when_new_slot_is_added()
    {
        AccountChanges ac = new(TestItem.AddressA);
        ac.GetOrAddSlotChanges(9u).AddStorageChange(new StorageChange(0u, 0x99));

        Assert.That(ac.StorageChanges[0].Key, Is.EqualTo((UInt256)9u));

        ac.GetOrAddSlotChanges(1u).AddStorageChange(new StorageChange(0u, 0x11));

        Assert.That(ac.StorageChanges[0].Key, Is.EqualTo((UInt256)1u));
        Assert.That(ac.StorageChanges[1].Key, Is.EqualTo((UInt256)9u));
    }

    [Test]
    public void Slot_changes_at_index_equal_is_independent_of_write_order()
    {
        AccountChanges left = new(TestItem.AddressA);
        left.GetOrAddSlotChanges(9u).AddStorageChange(new StorageChange(0u, 0x99));
        left.GetOrAddSlotChanges(1u).AddStorageChange(new StorageChange(0u, 0x11));

        AccountChanges right = new(TestItem.AddressA);
        right.GetOrAddSlotChanges(1u).AddStorageChange(new StorageChange(0u, 0x11));
        right.GetOrAddSlotChanges(9u).AddStorageChange(new StorageChange(0u, 0x99));

        Assert.That(left.SlotChangesAtIndexEqual(right, 0u), Is.True);
    }
}
