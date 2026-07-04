// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using NUnit.Framework;

namespace Nethermind.Core.Test.BlockAccessLists;

/// <summary>
/// Pins <see cref="BalPostStateDelta.Reduce"/>: read-only accounts are excluded, each field/slot
/// collapses to its last-indexed post-value, and zero storage values survive (zero = trie delete).
/// </summary>
[TestFixture]
public class BalPostStateDeltaTests
{
    [Test]
    public void T1_1_empty_bal_produces_no_accounts()
    {
        ReadOnlyBlockAccessList bal = Build.A.BlockAccessList.TestObject;

        BalPostStateDelta delta = BalPostStateDelta.Reduce(bal);

        Assert.That(delta.Accounts, Is.Empty);
    }

    [Test]
    public void T1_2_read_only_account_is_excluded()
    {
        ReadOnlyAccountChanges readOnly = Build.An.AccountChanges
            .WithAddress(TestItem.AddressA)
            .WithStorageReads(1, 2, 3)
            .TestObject;
        ReadOnlyBlockAccessList bal = Build.A.BlockAccessList.WithAccountChanges(readOnly).TestObject;

        BalPostStateDelta delta = BalPostStateDelta.Reduce(bal);

        Assert.That(delta.Accounts, Is.Empty);
    }

    private static System.Collections.Generic.IEnumerable<TestCaseData> BalanceLastWinsCases()
    {
        // T1.3 single change, T1.4 multi change: last index wins; other fields stay null.
        yield return new TestCaseData(new BalanceChange[] { new(0, 100) }, (UInt256)100).SetName("single_change");
        yield return new TestCaseData(
            new BalanceChange[] { new(0, 100), new(3, 300), new(7, 700) }, (UInt256)700).SetName("last_index_wins");
    }

    [TestCaseSource(nameof(BalanceLastWinsCases))]
    public void T1_3_4_balance_reduces_to_last_change(BalanceChange[] changes, UInt256 expected)
    {
        ReadOnlyAccountChanges ac = Build.An.AccountChanges
            .WithAddress(TestItem.AddressA)
            .WithBalanceChanges(changes)
            .TestObject;
        ReadOnlyBlockAccessList bal = Build.A.BlockAccessList.WithAccountChanges(ac).TestObject;

        BalPostStateDelta delta = BalPostStateDelta.Reduce(bal);

        Assert.That(delta.Accounts, Has.Length.EqualTo(1));
        BalPostStateDelta.AccountDelta d = delta.Accounts[0];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(d.Address, Is.EqualTo(TestItem.AddressA));
            Assert.That(d.Balance, Is.EqualTo(expected));
            Assert.That(d.Nonce, Is.Null);
            Assert.That(d.CodeHash, Is.Null);
            Assert.That(d.Storage, Is.Empty);
        }
    }

    [Test]
    public void T1_5_slot_last_index_wins()
    {
        UInt256 slot = 5;
        ReadOnlyAccountChanges ac = Build.An.AccountChanges
            .WithAddress(TestItem.AddressA)
            .WithStorageChanges(slot,
                new StorageChange(1, (UInt256)111),
                new StorageChange(4, (UInt256)444))
            .TestObject;
        ReadOnlyBlockAccessList bal = Build.A.BlockAccessList.WithAccountChanges(ac).TestObject;

        BalPostStateDelta delta = BalPostStateDelta.Reduce(bal);

        BalPostStateDelta.SlotWrite[] storage = delta.Accounts[0].Storage;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(storage, Has.Length.EqualTo(1));
            Assert.That(storage[0].Slot, Is.EqualTo(slot));
            Assert.That(storage[0].Value, Is.EqualTo(new StorageChange(4, (UInt256)444).Value));
        }
    }

    [Test]
    public void T1_6_slot_written_to_zero_keeps_zero_value()
    {
        UInt256 slot = 5;
        ReadOnlyAccountChanges ac = Build.An.AccountChanges
            .WithAddress(TestItem.AddressA)
            .WithStorageChanges(slot,
                new StorageChange(1, (UInt256)111),
                new StorageChange(4, UInt256.Zero))
            .TestObject;
        ReadOnlyBlockAccessList bal = Build.A.BlockAccessList.WithAccountChanges(ac).TestObject;

        BalPostStateDelta delta = BalPostStateDelta.Reduce(bal);

        BalPostStateDelta.SlotWrite[] storage = delta.Accounts[0].Storage;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(storage, Has.Length.EqualTo(1), "zero value must be kept: zero means trie delete downstream");
            Assert.That(storage[0].Slot, Is.EqualTo(slot));
            Assert.That(storage[0].Value, Is.EqualTo(new StorageChange(4, UInt256.Zero).Value));
        }
    }

    [Test]
    public void T1_7_code_change_reduces_to_code_hash()
    {
        byte[] code = [0x60, 0x00, 0x60, 0x01];
        ReadOnlyAccountChanges ac = Build.An.AccountChanges
            .WithAddress(TestItem.AddressA)
            .WithCodeChanges(new CodeChange(0, code))
            .TestObject;
        ReadOnlyBlockAccessList bal = Build.A.BlockAccessList.WithAccountChanges(ac).TestObject;

        BalPostStateDelta delta = BalPostStateDelta.Reduce(bal);

        Assert.That(delta.Accounts[0].CodeHash, Is.EqualTo(ValueKeccak.Compute(code)));
    }

    [Test]
    public void T1_7b_code_last_index_wins()
    {
        byte[] codeAt0 = [0x60, 0x00];
        byte[] codeAt2 = [0x60, 0x01, 0x60, 0x02];
        ReadOnlyAccountChanges ac = Build.An.AccountChanges
            .WithAddress(TestItem.AddressA)
            .WithCodeChanges(new CodeChange(0, codeAt0), new CodeChange(2, codeAt2))
            .TestObject;
        ReadOnlyBlockAccessList bal = Build.A.BlockAccessList.WithAccountChanges(ac).TestObject;

        BalPostStateDelta delta = BalPostStateDelta.Reduce(bal);

        // The later code change (index 2) must win; a [0] bug would return codeAt0's hash.
        Assert.That(delta.Accounts[0].CodeHash, Is.EqualTo(ValueKeccak.Compute(codeAt2)));
    }

    [Test]
    public void T1_8_mixed_account_reduces_all_fields()
    {
        ReadOnlyAccountChanges ac = Build.An.AccountChanges
            .WithAddress(TestItem.AddressA)
            .WithBalanceChanges(new BalanceChange(0, 100), new BalanceChange(2, 200))
            .WithNonceChanges(new NonceChange(0, 1), new NonceChange(2, 3))
            .WithStorageChanges(1, new StorageChange(1, (UInt256)11))
            .WithStorageChanges(2, new StorageChange(2, (UInt256)22))
            .TestObject;
        ReadOnlyBlockAccessList bal = Build.A.BlockAccessList.WithAccountChanges(ac).TestObject;

        BalPostStateDelta delta = BalPostStateDelta.Reduce(bal);

        BalPostStateDelta.AccountDelta d = delta.Accounts[0];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(d.Balance, Is.EqualTo((UInt256)200));
            Assert.That(d.Nonce, Is.EqualTo(3ul));
            Assert.That(d.CodeHash, Is.Null);
            Assert.That(d.Storage, Has.Length.EqualTo(2));
        }
    }

    [Test]
    public void T1_9_multiple_accounts_each_reduced()
    {
        ReadOnlyAccountChanges a = Build.An.AccountChanges
            .WithAddress(TestItem.AddressA)
            .WithBalanceChanges(new BalanceChange(0, 100))
            .TestObject;
        ReadOnlyAccountChanges b = Build.An.AccountChanges
            .WithAddress(TestItem.AddressB)
            .WithNonceChanges(new NonceChange(0, 7))
            .TestObject;
        ReadOnlyAccountChanges readOnly = Build.An.AccountChanges
            .WithAddress(TestItem.AddressC)
            .WithStorageReads(1)
            .TestObject;
        ReadOnlyBlockAccessList bal = Build.A.BlockAccessList
            .WithAccountChanges(a, b, readOnly)
            .TestObject;

        BalPostStateDelta delta = BalPostStateDelta.Reduce(bal);

        // AddressC is read-only and excluded; AddressA and AddressB survive (address-sorted).
        Assert.That(delta.Accounts, Has.Length.EqualTo(2));
        using (Assert.EnterMultipleScope())
        {
            BalPostStateDelta.AccountDelta da = System.Array.Find(delta.Accounts, x => x.Address == TestItem.AddressA);
            BalPostStateDelta.AccountDelta db = System.Array.Find(delta.Accounts, x => x.Address == TestItem.AddressB);
            Assert.That(da.Balance, Is.EqualTo((UInt256)100));
            Assert.That(da.Nonce, Is.Null);
            Assert.That(db.Nonce, Is.EqualTo(7ul));
            Assert.That(db.Balance, Is.Null);
        }
    }

    [Test]
    public void T1_10_out_of_order_changes_throw()
    {
        // Last-element-wins is only correct on strictly-ascending input; an unsorted array
        // must fail loudly rather than silently pick a wrong final value.
        ReadOnlyAccountChanges ac = Build.An.AccountChanges
            .WithAddress(TestItem.AddressA)
            .WithBalanceChanges(new BalanceChange(3, 300), new BalanceChange(1, 100))
            .TestObject;
        ReadOnlyBlockAccessList bal = Build.A.BlockAccessList.WithAccountChanges(ac).TestObject;

        Assert.That(() => BalPostStateDelta.Reduce(bal), Throws.InstanceOf<System.InvalidOperationException>());
    }

    [Test]
    public void T1_11_account_with_only_empty_slot_change_arrays_is_excluded()
    {
        // HasStateChanges is true (StorageChanges.Length > 0) but every slot has zero changes;
        // the account contributes nothing and must not emit a phantom empty AccountDelta.
        ReadOnlyAccountChanges ac = Build.An.AccountChanges
            .WithAddress(TestItem.AddressA)
            .WithStorageChanges(5)
            .TestObject;
        ReadOnlyBlockAccessList bal = Build.A.BlockAccessList.WithAccountChanges(ac).TestObject;

        Assert.That(ac.HasStateChanges, Is.True, "guards the shape under test");
        BalPostStateDelta delta = BalPostStateDelta.Reduce(bal);

        Assert.That(delta.Accounts, Is.Empty);
    }
}
