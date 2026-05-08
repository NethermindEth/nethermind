// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using NUnit.Framework;

namespace Nethermind.Core.Test.BlockAccessLists;

[TestFixture]
public class BlockAccessListJournalTests
{
    [Test]
    public void Restore_reinstates_previous_values_for_interleaved_change_types()
    {
        BlockAccessList bal = new();
        bal.Index = 1;

        byte[] emptyCode = [];
        byte[] codeBeforeSnapshot = [0x60];
        byte[] codeAfterSnapshot = [0x61];
        UInt256 slot = 7;

        bal.AddBalanceChange(TestItem.AddressA, before: 0, after: 10);
        bal.AddNonceChange(TestItem.AddressA, 1);
        bal.AddCodeChange(TestItem.AddressA, emptyCode, codeBeforeSnapshot);
        bal.AddStorageChange(TestItem.AddressA, slot, before: 0, after: 11);

        int snapshot = bal.TakeSnapshot();

        bal.AddBalanceChange(TestItem.AddressA, before: 10, after: 20);
        bal.AddNonceChange(TestItem.AddressA, 2);
        bal.AddCodeChange(TestItem.AddressA, codeBeforeSnapshot, codeAfterSnapshot);
        bal.AddStorageChange(TestItem.AddressA, slot, before: 11, after: 22);

        bal.Restore(snapshot);

        AccountChanges accountChanges = bal.GetAccountChanges(TestItem.AddressA)!;
        Assert.That(accountChanges.BalanceChangeAtIndex(1)!.Value.Value, Is.EqualTo((UInt256)10));
        Assert.That(accountChanges.NonceChangeAtIndex(1)!.Value.Value, Is.EqualTo(1));
        Assert.That(accountChanges.CodeChangeAtIndex(1)!.Value.Code, Is.EqualTo(codeBeforeSnapshot));

        Assert.That(accountChanges.TryGetSlotChanges(slot, out SlotChanges? slotChanges), Is.True);
        Assert.That(slotChanges!.Changes[1].Value, Is.EqualTo(((UInt256)11).ToBigEndianWord()));
    }

    [Test]
    public void Restore_after_delete_account_preserves_prestate_index_entries()
    {
        UInt256 slot = 9;
        byte[] prestateCode = [0x60, 0x00];
        BlockAccessList bal = CreateBalWithPrestate(TestItem.AddressA, slot, prestateCode);
        bal.Index = 1;

        int snapshot = bal.TakeSnapshot();

        bal.DeleteAccount(TestItem.AddressA, oldBalance: 100);
        bal.Restore(snapshot);

        AccountChanges accountChanges = bal.GetAccountChanges(TestItem.AddressA)!;
        Assert.That(accountChanges.BalanceChangeAtIndex(Eip7928Constants.PrestateIndex)!.Value.Value, Is.EqualTo((UInt256)100));
        Assert.That(accountChanges.NonceChangeAtIndex(Eip7928Constants.PrestateIndex)!.Value.Value, Is.EqualTo(5));
        Assert.That(accountChanges.CodeChangeAtIndex(Eip7928Constants.PrestateIndex)!.Value.Code, Is.EqualTo(prestateCode));

        Assert.That(accountChanges.TryGetSlotChanges(slot, out SlotChanges? slotChanges), Is.True);
        Assert.That(slotChanges!.Changes[Eip7928Constants.PrestateIndex].Value, Is.EqualTo(((UInt256)77).ToBigEndianWord()));
    }

    [Test]
    public void Restore_after_delete_account_restores_prestate_then_real_indices()
    {
        UInt256 slot = 9;
        BlockAccessList bal = CreateBalWithPrestate(TestItem.AddressA, slot, [0x60, 0x00]);
        AccountChanges accountChanges = bal.GetAccountChanges(TestItem.AddressA)!;
        accountChanges.AddBalanceChange(new(0u, 101));
        accountChanges.AddNonceChange(new(0u, 6));
        accountChanges.AddCodeChange(new(0u, [0x60, 0x01]));
        accountChanges.GetOrAddSlotChanges(slot).AddStorageChange(new(0u, 88));
        accountChanges.AddBalanceChange(new(2u, 102));
        accountChanges.AddNonceChange(new(2u, 7));
        accountChanges.AddCodeChange(new(2u, [0x60, 0x02]));
        accountChanges.GetOrAddSlotChanges(slot).AddStorageChange(new(2u, 99));
        bal.Index = 3;

        int snapshot = bal.TakeSnapshot();

        bal.DeleteAccount(TestItem.AddressA, oldBalance: 102);
        bal.Restore(snapshot);

        Assert.That(accountChanges.BalanceChanges[0].Index, Is.EqualTo(Eip7928Constants.PrestateIndex));
        Assert.That(accountChanges.BalanceChanges[1].Index, Is.EqualTo(0u));
        Assert.That(accountChanges.BalanceChanges[2].Index, Is.EqualTo(2u));
        Assert.That(accountChanges.NonceChanges[0].Index, Is.EqualTo(Eip7928Constants.PrestateIndex));
        Assert.That(accountChanges.NonceChanges[1].Index, Is.EqualTo(0u));
        Assert.That(accountChanges.NonceChanges[2].Index, Is.EqualTo(2u));
        Assert.That(accountChanges.CodeChanges[0].Index, Is.EqualTo(Eip7928Constants.PrestateIndex));
        Assert.That(accountChanges.CodeChanges[1].Index, Is.EqualTo(0u));
        Assert.That(accountChanges.CodeChanges[2].Index, Is.EqualTo(2u));

        Assert.That(accountChanges.TryGetSlotChanges(slot, out SlotChanges? slotChanges), Is.True);
        Assert.That(slotChanges!.Changes.Keys[0], Is.EqualTo(Eip7928Constants.PrestateIndex));
        Assert.That(slotChanges.Changes.Keys[1], Is.EqualTo(0u));
        Assert.That(slotChanges.Changes.Keys[2], Is.EqualTo(2u));
        Assert.That(slotChanges.Changes.Values[2].Value, Is.EqualTo(((UInt256)99).ToBigEndianWord()));
    }

    private static BlockAccessList CreateBalWithPrestate(Address address, UInt256 slot, byte[] prestateCode)
    {
        AccountChanges accountChanges = new(address);
        accountChanges.AddBalanceChange(new(Eip7928Constants.PrestateIndex, 100));
        accountChanges.AddNonceChange(new(Eip7928Constants.PrestateIndex, 5));
        accountChanges.AddCodeChange(new(Eip7928Constants.PrestateIndex, prestateCode));
        accountChanges.GetOrAddSlotChanges(slot).AddStorageChange(new(Eip7928Constants.PrestateIndex, 77));

        BlockAccessList bal = new();
        bal.AddAccountChanges(accountChanges);
        return bal;
    }
}
