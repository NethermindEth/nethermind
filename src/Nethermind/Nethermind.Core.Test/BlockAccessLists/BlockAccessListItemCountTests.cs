// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Reflection;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using NUnit.Framework;
using CoreCollectionExtensions = Nethermind.Core.Collections.CollectionExtensions;

namespace Nethermind.Core.Test.BlockAccessLists;

[TestFixture]
public class BlockAccessListItemCountTests
{
    public static IEnumerable<TestCaseData> ItemCountCases()
    {
        // Item count is 1 per account + 1 per distinct storage key, changed or read (matches
        // ReadOnlyBlockAccessList.ItemCount and what BlockValidator.ValidateBlockLevelAccessListSize consumes).
        yield return new TestCaseData(
            (Action<BlockAccessIndexRecorder>)(static _ => { }),
            0).SetName("ItemCount_empty_is_zero");
        yield return new TestCaseData(
            (Action<BlockAccessIndexRecorder>)(static r => r.AddAccountRead(TestItem.AddressA)),
            1).SetName("ItemCount_account_read_counts_account");
        yield return new TestCaseData(
            (Action<BlockAccessIndexRecorder>)(static r => r.AddBalanceChange(TestItem.AddressA, before: UInt256.Zero, after: (UInt256)100)),
            1).SetName("ItemCount_balance_change_counts_only_account");
        yield return new TestCaseData(
            (Action<BlockAccessIndexRecorder>)(static r => r.AddNonceChange(TestItem.AddressA, 1)),
            1).SetName("ItemCount_nonce_change_counts_only_account");
        yield return new TestCaseData(
            (Action<BlockAccessIndexRecorder>)(static r => r.AddCodeChange(TestItem.AddressA, before: Array.Empty<byte>(), after: new byte[] { 0x60 })),
            1).SetName("ItemCount_code_change_counts_only_account");
        yield return new TestCaseData(
            (Action<BlockAccessIndexRecorder>)(static r => r.AddStorageChange(new StorageCell(TestItem.AddressA, 1), before: UInt256.Zero, after: (UInt256)42)),
            2).SetName("ItemCount_storage_change_counts_account_plus_slot");
        yield return new TestCaseData(
            (Action<BlockAccessIndexRecorder>)(static r => r.AddStorageRead(new StorageCell(TestItem.AddressA, 1))),
            2).SetName("ItemCount_storage_read_counts_account_plus_slot");
        yield return new TestCaseData(
            (Action<BlockAccessIndexRecorder>)(static r =>
            {
                r.AddStorageChange(new StorageCell(TestItem.AddressA, 1), before: UInt256.Zero, after: (UInt256)42);
                r.AddStorageRead(new StorageCell(TestItem.AddressA, 2));
                r.AddBalanceChange(TestItem.AddressB, before: UInt256.Zero, after: (UInt256)1);
                r.AddStorageRead(new StorageCell(TestItem.AddressB, 7));
                r.AddStorageRead(new StorageCell(TestItem.AddressB, 7));
                r.AddStorageRead(new StorageCell(TestItem.AddressA, 1));
                r.AddStorageRead(new StorageCell(TestItem.AddressA, 2));
            }),
            5).SetName("ItemCount_combines_storage_changes_and_reads_across_accounts");
    }

    [TestCaseSource(nameof(ItemCountCases))]
    public void ItemCount_counts_accounts_and_distinct_storage_keys(Action<BlockAccessIndexRecorder> record, int expected)
    {
        BlockAccessIndexRecorder recorder = new();
        recorder.Begin(0);
        record(recorder);
        BlockAccessListItemSet items = new();
        items.Add(recorder.Log, recorder.Start);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(items.ItemCount, Is.EqualTo(expected));
            Assert.That(Merge(recorder.Log).ItemCount, Is.EqualTo(expected));
        }
    }

    /// <summary>
    /// The item set and the log are reused across blocks through <c>Clear</c>;
    /// the next block's <c>ItemCount</c> must report a fresh count, never the stale prior one.
    /// </summary>
    [Test]
    public void ItemCount_reflects_reuse_across_blocks_through_Clear()
    {
        BlockAccessIndexRecorder recorder = new();
        BlockAccessListItemSet items = new();
        recorder.Begin(0);
        recorder.AddStorageRead(new StorageCell(TestItem.AddressA, 1));
        recorder.AddStorageRead(new StorageCell(TestItem.AddressA, 2));
        items.Add(recorder.Log, recorder.Start);
        Assert.That(items.ItemCount, Is.EqualTo(3), "block 1: 1 account + 2 storage reads");

        recorder.Log.Clear();
        items.Clear();
        recorder.Begin(0);
        for (int slot = 1; slot <= 4; slot++) recorder.AddStorageRead(new StorageCell(TestItem.AddressB, (UInt256)slot));
        items.Add(recorder.Log, recorder.Start);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(items.ItemCount, Is.EqualTo(5), "block 2: 1 account + 4 storage reads, no carryover from block 1");
            Assert.That(Merge(recorder.Log).ItemCount, Is.EqualTo(5));
        }
    }

    public static IEnumerable<TestCaseData> CountNewItemsCases()
    {
        yield return new TestCaseData(
            (Action<BlockAccessIndexRecorder>)(static r => r.AddAccountRead(TestItem.AddressA)),
            0).SetName("CountNewItems_existing_account_read_adds_nothing");
        yield return new TestCaseData(
            (Action<BlockAccessIndexRecorder>)(static r => r.AddAccountRead(TestItem.AddressC)),
            1).SetName("CountNewItems_new_account_adds_one");
        yield return new TestCaseData(
            (Action<BlockAccessIndexRecorder>)(static r =>
            {
                r.AddStorageRead(new StorageCell(TestItem.AddressA, 1));
                r.AddStorageRead(new StorageCell(TestItem.AddressA, 2));
                r.AddStorageChange(new StorageCell(TestItem.AddressB, 7), before: UInt256.Zero, after: UInt256.One);
            }),
            0).SetName("CountNewItems_existing_keys_add_nothing_whether_read_or_written");
        yield return new TestCaseData(
            (Action<BlockAccessIndexRecorder>)(static r =>
            {
                r.AddStorageRead(new StorageCell(TestItem.AddressA, 3));
                r.AddStorageChange(new StorageCell(TestItem.AddressA, 4), before: UInt256.Zero, after: UInt256.One);
                r.AddStorageRead(new StorageCell(TestItem.AddressA, 4));
            }),
            2).SetName("CountNewItems_new_keys_of_existing_account_count_once_each");
        yield return new TestCaseData(
            (Action<BlockAccessIndexRecorder>)(static r =>
            {
                r.AddStorageChange(new StorageCell(TestItem.AddressC, 1), before: UInt256.Zero, after: UInt256.One);
                r.AddStorageRead(new StorageCell(TestItem.AddressC, 1));
                r.AddStorageRead(new StorageCell(TestItem.AddressC, 2));
                r.AddBalanceChange(TestItem.AddressD, before: UInt256.Zero, after: UInt256.One);
            }),
            4).SetName("CountNewItems_new_accounts_count_with_their_keys");
    }

    [TestCaseSource(nameof(CountNewItemsCases))]
    public void CountNewItems_matches_the_item_count_Add_adds(Action<BlockAccessIndexRecorder> record, int expected)
    {
        BlockAccessIndexRecorder recorder = new();
        BlockAccessListItemSet items = new();
        recorder.Begin(1);
        recorder.AddStorageRead(new StorageCell(TestItem.AddressA, 1));
        recorder.AddStorageChange(new StorageCell(TestItem.AddressA, 2), before: UInt256.Zero, after: UInt256.One);
        recorder.AddStorageRead(new StorageCell(TestItem.AddressB, 7));
        items.Add(recorder.Log, recorder.Start);
        int before = items.ItemCount;

        recorder.Begin(2);
        record(recorder);
        int counted = items.CountNewItems(recorder.Log, recorder.Start);
        items.Add(recorder.Log, recorder.Start);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(counted, Is.EqualTo(expected));
            Assert.That(items.ItemCount - before, Is.EqualTo(counted));
        }
    }

    [Test]
    public void Clear_trims_oversized_account_set()
    {
        const int OversizedCapacity = CoreCollectionExtensions.DefaultTrimAboveCapacity + 1;

        BlockAccessListItemSet items = new();
        object accounts = typeof(BlockAccessListItemSet).GetField(
            "_accounts",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(items)!;
        MethodInfo ensureCapacity = accounts.GetType().GetMethod(
            nameof(HashSet<>.EnsureCapacity),
            [typeof(int)])!;
        PropertyInfo capacity = accounts.GetType().GetProperty(nameof(HashSet<>.Capacity))!;
        ensureCapacity.Invoke(accounts, [OversizedCapacity]);
        int capacityBeforeClear = (int)capacity.GetValue(accounts)!;

        items.Clear();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(capacityBeforeClear, Is.GreaterThan(CoreCollectionExtensions.DefaultTrimAboveCapacity));
            Assert.That(capacity.GetValue(accounts), Is.GreaterThan(0));
            Assert.That(capacity.GetValue(accounts), Is.LessThan(capacityBeforeClear));
        }
    }

    /// <summary>
    /// <see cref="ReadOnlyBlockAccessList.ItemCount"/> is set at construction by the
    /// RLP decoder (which already walked every entry to validate ordering) and is immutable
    /// thereafter. The validator's size check reads it on the suggested-block side, so the
    /// stored value must match the underlying wire shape.
    /// </summary>
    [Test]
    public void ReadOnlyItemCount_set_at_construction_and_immutable()
    {
        ReadOnlyAccountChanges a = Build.An.AccountChanges
            .WithAddress(TestItem.AddressA)
            .WithStorageReads((UInt256)1, (UInt256)2)
            .TestObject;
        ReadOnlyAccountChanges b = Build.An.AccountChanges
            .WithAddress(TestItem.AddressB)
            .WithBalanceChanges(new BalanceChange(0, 1))
            .TestObject;
        ReadOnlyBlockAccessList bal = Build.A.BlockAccessList
            .WithAccountChanges(a, b)
            .TestObject;

        Assert.That(bal.ItemCount, Is.EqualTo(4));
    }

    private static MergedBlockAccessList Merge(BlockAccessListLog log)
    {
        log.Seal();
        MergedBlockAccessList merged = new();
        merged.Merge([log]);
        return merged;
    }
}
