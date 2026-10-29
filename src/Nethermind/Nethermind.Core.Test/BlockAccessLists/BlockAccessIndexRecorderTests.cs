// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using NUnit.Framework;

namespace Nethermind.Core.Test.BlockAccessLists;

/// <summary>
/// Change and snapshot semantics of <see cref="BlockAccessIndexRecorder"/>, read back through the block list its log merges into.
/// </summary>
[TestFixture]
public class BlockAccessIndexRecorderTests
{
    // Code set and then removed within one transaction is no change, even when the code before it was not given.
    [Test]
    public void AddCodeChange_records_nothing_for_code_that_ends_as_it_started()
    {
        BlockAccessIndexRecorder recorder = new();
        recorder.Begin(0);
        byte[] code = [0x60];

        recorder.AddCodeChange(TestItem.AddressA, Array.Empty<byte>(), Array.Empty<byte>());
        recorder.AddCodeChange(TestItem.AddressB, default, code);
        recorder.AddCodeChange(TestItem.AddressB, code, Array.Empty<byte>());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(recorder.HasAccount(TestItem.AddressA), Is.False);
            Assert.That(recorder.GetAccountChanges(TestItem.AddressB)!.CodeChange, Is.Null);
        }
    }

    [Test]
    public void Restore_reinstates_previous_values_for_interleaved_change_types()
    {
        BlockAccessIndexRecorder recorder = new();
        recorder.Begin(1);

        byte[] emptyCode = [];
        byte[] codeBeforeSnapshot = [0x60];
        byte[] codeAfterSnapshot = [0x61];
        StorageCell cell = new(TestItem.AddressA, 7);

        recorder.AddBalanceChange(TestItem.AddressA, before: 0, after: 10);
        recorder.AddNonceChange(TestItem.AddressA, 1);
        recorder.AddCodeChange(TestItem.AddressA, emptyCode, codeBeforeSnapshot);
        recorder.AddStorageChange(cell, before: 0, after: 11);

        int snapshot = recorder.TakeSnapshot();

        recorder.AddBalanceChange(TestItem.AddressA, before: 10, after: 20);
        recorder.AddNonceChange(TestItem.AddressA, 2);
        recorder.AddCodeChange(TestItem.AddressA, codeBeforeSnapshot, codeAfterSnapshot);
        recorder.AddStorageChange(cell, before: 11, after: 22);

        recorder.Restore(snapshot);

        RecordedAccountChanges accountChanges = recorder.GetAccountChanges(TestItem.AddressA)!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(accountChanges.BalanceChange!.Value.Value, Is.EqualTo((UInt256)10));
            Assert.That(accountChanges.NonceChange!.Value.Value, Is.EqualTo(1u));
            Assert.That(accountChanges.CodeChange!.Value.Code, Is.EqualTo(codeBeforeSnapshot));
            Assert.That(accountChanges.StorageChanges[cell.Index].Value, Is.EqualTo((UInt256)11));
        }
    }

    [Test]
    public void Restore_after_delete_account_restores_within_block_change_entries([Values(0, 1, 64)] int slotCount)
    {
        BlockAccessIndexRecorder recorder = new();
        recorder.Begin(1);
        recorder.AddBalanceChange(TestItem.AddressA, before: 0, after: 50);
        recorder.AddNonceChange(TestItem.AddressA, 3);
        recorder.AddCodeChange(TestItem.AddressA, before: Array.Empty<byte>(), after: new byte[] { 0x60, 0x01 });
        recorder.AddStorageRead(new StorageCell(TestItem.AddressA, 999));
        for (int i = 0; i < slotCount; i++)
            recorder.AddStorageChange(new StorageCell(TestItem.AddressA, (UInt256)i), before: 0, after: (UInt256)(77 + i));

        int snapshot = recorder.TakeSnapshot();

        recorder.DeleteAccount(TestItem.AddressA, oldBalance: 50);
        RecordedAccountChanges deleted = recorder.GetAccountChanges(TestItem.AddressA)!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(deleted.StorageChangeCount, Is.Zero);
            Assert.That(deleted.StorageReads, Has.Count.EqualTo(slotCount + 1));
            for (int i = 0; i < slotCount; i++)
                Assert.That(deleted.StorageReads, Does.Contain((UInt256)i));
        }

        recorder.Restore(snapshot);
        RecordedAccountChanges restored = recorder.GetAccountChanges(TestItem.AddressA)!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(restored.BalanceChange!.Value.Value, Is.EqualTo((UInt256)50));
            Assert.That(restored.NonceChange!.Value.Value, Is.EqualTo(3u));
            Assert.That(restored.CodeChange!.Value.Code, Is.EqualTo(new byte[] { 0x60, 0x01 }));

            Assert.That(restored.StorageChangeCount, Is.EqualTo(slotCount));
            Assert.That(restored.StorageReads, Is.EquivalentTo(new UInt256[] { 999 }));
            for (int i = 0; i < slotCount; i++)
                Assert.That(restored.StorageChanges[(UInt256)i].Value, Is.EqualTo((UInt256)(77 + i)));
        }
    }

    [Test]
    public void Restore_to_the_start_clears_every_change_made_at_the_index()
    {
        StorageCell cell = new(TestItem.AddressA, 4);
        BlockAccessIndexRecorder recorder = new();
        recorder.Begin(1);

        int empty = recorder.TakeSnapshot();

        recorder.AddBalanceChange(TestItem.AddressA, before: 0, after: 1);
        recorder.AddNonceChange(TestItem.AddressA, 9);
        recorder.AddStorageChange(cell, before: 0, after: 0x42);

        recorder.Restore(empty);

        RecordedAccountChanges? accountChanges = recorder.GetAccountChanges(TestItem.AddressA);
        Assert.That(accountChanges, Is.Not.Null, "the account stays accessed; only its changes revert");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(accountChanges!.BalanceChange, Is.Null);
            Assert.That(accountChanges.NonceChange, Is.Null);
            Assert.That(accountChanges.HasStorageChange(cell.Index), Is.False);
        }
    }

    [Test]
    public void Storage_overwrites_preserve_snapshot_and_read_membership([Values] bool returnToOriginal)
    {
        BlockAccessIndexRecorder recorder = new();
        recorder.Begin(3);
        StorageCell cell = new(TestItem.AddressA, UInt256.MaxValue);
        recorder.AddStorageRead(cell);
        recorder.AddStorageChange(cell, 7, 11);
        recorder.AddStorageRead(cell);
        int snapshot = recorder.TakeSnapshot();
        recorder.AddStorageChange(cell, 11, 13);
        UInt256 last = returnToOriginal ? 7u : 17u;
        recorder.AddStorageChange(cell, 13, in last);

        RecordedAccountChanges account = recorder.GetAccountChanges(TestItem.AddressA)!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(account.HasStorageChange(cell.Index), Is.EqualTo(!returnToOriginal));
            Assert.That(account.StorageReads.Contains(cell.Index), Is.EqualTo(returnToOriginal));
        }
        if (!returnToOriginal) Assert.That(account.StorageChanges[cell.Index].Value, Is.EqualTo(last));

        recorder.AddStorageChange(cell, in last, 19);
        account = recorder.GetAccountChanges(TestItem.AddressA)!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(account.StorageChanges[cell.Index].Value, Is.EqualTo((UInt256)19));
            Assert.That(account.StorageReads, Does.Not.Contain(cell.Index));
        }

        recorder.Restore(snapshot);
        account = recorder.GetAccountChanges(TestItem.AddressA)!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(account.StorageChanges[cell.Index].Value, Is.EqualTo((UInt256)11));
            Assert.That(account.StorageChanges[cell.Index].Index, Is.EqualTo(3u));
            Assert.That(account.StorageReads, Does.Not.Contain(cell.Index));
        }
    }

    [Test]
    public void Restore_unwinds_interleaved_writes_between_nested_snapshots([Values] bool separateAccounts)
    {
        BlockAccessIndexRecorder recorder = new();
        recorder.Begin(1);
        StorageCell[] cells = [new(TestItem.AddressA, 0), new(TestItem.AddressA, UInt256.MaxValue),
            new(separateAccounts ? TestItem.AddressB : TestItem.AddressA, 3)];
        int empty = recorder.TakeSnapshot();
        WriteRepeated(7, 11);
        int outer = recorder.TakeSnapshot();
        WriteRepeated(11, 7);
        int inner = recorder.TakeSnapshot();
        AssertValue(7);

        WriteRepeated(7, 29);
        recorder.Restore(inner);
        AssertValue(7);
        WriteRepeated(7, 53);
        recorder.Restore(inner);
        AssertValue(7);
        recorder.Restore(outer);
        AssertValue(11);
        recorder.Restore(empty);
        AssertValue(7);

        recorder.Discard();
        recorder.Begin(1);
        WriteRepeated(7, 11);
        recorder.Restore(0);
        AssertValue(7);

        void WriteRepeated(uint before, uint after)
        {
            UInt256 current = before;
            for (uint repeat = 0; repeat < 64; repeat++)
            {
                UInt256 next = repeat == 63 ? after : repeat + 100;
                foreach (StorageCell cell in cells) recorder.AddStorageChange(in cell, in current, in next);
                current = next;
            }
        }

        void AssertValue(uint expected)
        {
            foreach (StorageCell cell in cells)
            {
                RecordedAccountChanges account = recorder.GetAccountChanges(cell.Address)!;
                if (expected == 7)
                {
                    using (Assert.EnterMultipleScope())
                    {
                        Assert.That(account.HasStorageChange(cell.Index), Is.False);
                        Assert.That(account.StorageReads, Does.Contain(cell.Index));
                    }
                }
                else Assert.That(account.StorageChanges[cell.Index].Value, Is.EqualTo((UInt256)expected));
            }
        }
    }
}
