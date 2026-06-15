// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using NUnit.Framework;

namespace Nethermind.Core.Test.BlockAccessLists;

/// <summary>
/// Journal/snapshot tests for the per-tx <see cref="BlockAccessListAtIndex"/> slice. The slice's
/// mutators (<see cref="BlockAccessListAtIndex.AddBalanceChange"/>, <c>AddNonceChange</c>,
/// <c>AddCodeChange</c>, <c>AddStorageChange</c>, <c>DeleteAccount</c>) push undo records onto an
/// internal <c>_changes</c> log so <see cref="BlockAccessListAtIndex.Restore"/> can revert to a
/// prior snapshot — exercised by EVM revert paths through <see cref="Nethermind.State.TracedAccessWorldState"/>.
/// </summary>
[TestFixture]
public class BlockAccessListJournalTests
{
    [Test]
    public void AddCodeChange_with_equal_before_after_does_not_create_account_changes()
    {
        BlockAccessListAtIndex slice = new() { Index = 0 };
        byte[] emptyCode = [];

        slice.AddCodeChange(TestItem.AddressA, emptyCode, emptyCode);

        Assert.That(slice.GetAccountChanges(TestItem.AddressA), Is.Null);
    }

    [Test]
    public void Restore_reinstates_previous_values_for_interleaved_change_types()
    {
        BlockAccessListAtIndex slice = new() { Index = 1 };

        byte[] emptyCode = [];
        byte[] codeBeforeSnapshot = [0x60];
        byte[] codeAfterSnapshot = [0x61];
        UInt256 slot = 7;

        slice.AddBalanceChange(TestItem.AddressA, before: 0, after: 10);
        slice.AddNonceChange(TestItem.AddressA, 1);
        slice.AddCodeChange(TestItem.AddressA, emptyCode, codeBeforeSnapshot);
        slice.AddStorageChange(TestItem.AddressA, slot, before: 0, after: 11);

        int snapshot = slice.TakeSnapshot();

        slice.AddBalanceChange(TestItem.AddressA, before: 10, after: 20);
        slice.AddNonceChange(TestItem.AddressA, 2);
        slice.AddCodeChange(TestItem.AddressA, codeBeforeSnapshot, codeAfterSnapshot);
        slice.AddStorageChange(TestItem.AddressA, slot, before: 11, after: 22);

        slice.Restore(snapshot);

        AccountChangesAtIndex accountChanges = slice.GetAccountChanges(TestItem.AddressA)!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(accountChanges.BalanceChange!.Value.Value, Is.EqualTo((UInt256)10));
            Assert.That(accountChanges.NonceChange!.Value.Value, Is.EqualTo(1u));
            Assert.That(accountChanges.CodeChange!.Value.Code, Is.EqualTo(codeBeforeSnapshot));

            Assert.That(accountChanges.TryGetStorageChange(slot, out StorageChange? slotChange), Is.True);
            Assert.That(slotChange!.Value.Value, Is.EqualTo(((UInt256)11).ToBigEndianWord()));
        }
    }

    [Test]
    public void Restore_after_delete_account_restores_within_block_change_entries()
    {
        UInt256 slot = 9;
        BlockAccessListAtIndex slice = new() { Index = 1 };
        slice.AddBalanceChange(TestItem.AddressA, before: 0, after: 50);
        slice.AddNonceChange(TestItem.AddressA, 3);
        slice.AddCodeChange(TestItem.AddressA, before: [], after: new byte[] { 0x60, 0x01 });
        slice.AddStorageChange(TestItem.AddressA, slot, before: 0, after: 77);

        int snapshot = slice.TakeSnapshot();

        slice.DeleteAccount(TestItem.AddressA, oldBalance: 50);
        slice.Restore(snapshot);

        AccountChangesAtIndex accountChanges = slice.GetAccountChanges(TestItem.AddressA)!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(accountChanges.BalanceChange!.Value.Value, Is.EqualTo((UInt256)50));
            Assert.That(accountChanges.NonceChange!.Value.Value, Is.EqualTo(3u));
            Assert.That(accountChanges.CodeChange!.Value.Code, Is.EqualTo(new byte[] { 0x60, 0x01 }));

            Assert.That(accountChanges.TryGetStorageChange(slot, out StorageChange? slotChange), Is.True);
            Assert.That(slotChange!.Value.Value, Is.EqualTo(((UInt256)77).ToBigEndianWord()));
        }
    }

    [Test]
    public void Restore_to_zero_clears_every_change_made_in_the_slice()
    {
        // Snapshot at 0 represents "before any mutation".
        UInt256 slot = 4;
        BlockAccessListAtIndex slice = new() { Index = 1 };

        int empty = slice.TakeSnapshot();

        slice.AddBalanceChange(TestItem.AddressA, before: 0, after: 1);
        slice.AddNonceChange(TestItem.AddressA, 9);
        slice.AddStorageChange(TestItem.AddressA, slot, before: 0, after: 0x42);

        slice.Restore(empty);

        AccountChangesAtIndex? accountChanges = slice.GetAccountChanges(TestItem.AddressA);
        Assert.That(accountChanges, Is.Not.Null, "the AccountChangesAtIndex entry persists; only the change fields revert");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(accountChanges!.BalanceChange, Is.Null);
            Assert.That(accountChanges.NonceChange, Is.Null);
            Assert.That(accountChanges.TryGetStorageChange(slot, out _), Is.False);
        }
    }
}
