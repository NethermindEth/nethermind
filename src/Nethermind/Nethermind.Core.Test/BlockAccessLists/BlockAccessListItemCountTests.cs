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
    [Test]
    public void ItemCount_empty_is_zero()
    {
        GeneratedBlockAccessList bal = new();
        Assert.That(bal.ItemCount, Is.EqualTo(0));
    }

    [Test]
    public void ItemCount_one_account_read_only_counts_account()
    {
        BlockAccessIndexRecorder slice = new();
        slice.AddAccountRead(TestItem.AddressA);

        GeneratedBlockAccessList bal = slice.ToGeneratedBlockAccessList();

        Assert.That(bal.ItemCount, Is.EqualTo(1));
    }

    public static IEnumerable<TestCaseData> SingleMutationCases()
    {
        // Item count is 1 per account + 1 per storage change + 1 per storage read (matches
        // ReadOnlyBlockAccessList.ItemCount and what BlockValidator.ValidateBlockLevelAccessListSize consumes).
        yield return new TestCaseData(
            (Action<BlockAccessIndexRecorder>)(static s => s.AddBalanceChange(TestItem.AddressA, before: UInt256.Zero, after: (UInt256)100)),
            1).SetName("ItemCount_balance_change_counts_only_account");
        yield return new TestCaseData(
            (Action<BlockAccessIndexRecorder>)(static s => s.AddNonceChange(TestItem.AddressA, 1)),
            1).SetName("ItemCount_nonce_change_counts_only_account");
        yield return new TestCaseData(
            (Action<BlockAccessIndexRecorder>)(static s => s.AddCodeChange(TestItem.AddressA, before: Array.Empty<byte>(), after: new byte[] { 0x60 })),
            1).SetName("ItemCount_code_change_counts_only_account");
        yield return new TestCaseData(
            (Action<BlockAccessIndexRecorder>)(static s => s.AddStorageChange(TestItem.AddressA, 1, before: UInt256.Zero, after: (UInt256)42)),
            2).SetName("ItemCount_storage_change_counts_account_plus_slot");
        yield return new TestCaseData(
            (Action<BlockAccessIndexRecorder>)(static s => s.AddStorageRead(TestItem.AddressA, 1)),
            2).SetName("ItemCount_storage_read_counts_account_plus_slot");
    }

    [TestCaseSource(nameof(SingleMutationCases))]
    public void ItemCount_after_single_mutation(Action<BlockAccessIndexRecorder> mutate, int expected)
    {
        BlockAccessIndexRecorder slice = new();
        mutate(slice);

        GeneratedBlockAccessList bal = slice.ToGeneratedBlockAccessList();

        Assert.That(bal.ItemCount, Is.EqualTo(expected));
    }

    [Test]
    public void ItemCount_combines_storage_changes_and_reads_across_accounts()
    {
        BlockAccessIndexRecorder slice = new();
        slice.AddStorageChange(TestItem.AddressA, 1, before: UInt256.Zero, after: (UInt256)42);
        slice.AddStorageRead(TestItem.AddressA, 2);
        slice.AddBalanceChange(TestItem.AddressB, before: UInt256.Zero, after: (UInt256)1);
        slice.AddStorageRead(TestItem.AddressB, 7);
        slice.AddStorageRead(TestItem.AddressB, 7);
        slice.AddStorageRead(new StorageCell(TestItem.AddressA, 1));
        slice.AddStorageRead(new StorageCell(TestItem.AddressA, 2));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(slice.GetAccountChanges(TestItem.AddressA)!.StorageReads, Is.EquivalentTo(new UInt256[] { 2 }));
            Assert.That(slice.GetAccountChanges(TestItem.AddressB)!.StorageReads, Is.EquivalentTo(new UInt256[] { 7 }));
        }

        GeneratedBlockAccessList bal = slice.ToGeneratedBlockAccessList();

        // A: account + storage change + storage read = 3; B: account + storage read = 2.
        Assert.That(bal.ItemCount, Is.EqualTo(5));
    }

    /// <summary>
    /// The item set and the log are reused across blocks via <see cref="BlockAccessListItemSet.Clear"/>;
    /// the next block's <c>ItemCount</c> must report a fresh count, never the stale prior one.
    /// </summary>
    [Test]
    public void ItemCount_reflects_reuse_across_blocks_through_Reset([Values] bool reuseSlice)
    {
        BlockAccessIndexRecorder sliceBlock1 = new();
        sliceBlock1.AddStorageRead(TestItem.AddressA, 1);
        sliceBlock1.AddStorageRead(TestItem.AddressA, 2);

        BlockAccessListItemSet bal = new();
        bal.TrackAddedEntries(sliceBlock1.Log, sliceBlock1.Start);
        Assert.That(bal.ItemCount, Is.EqualTo(3), "block 1: 1 account + 2 storage reads");

        bal.Clear();

        BlockAccessIndexRecorder sliceBlock2;
        if (reuseSlice)
        {
            sliceBlock1.Log.Clear();
            sliceBlock1.Begin(0);
            sliceBlock2 = sliceBlock1;
            sliceBlock2.AddStorageRead(TestItem.AddressA, 99);
            sliceBlock2.Discard();
        }
        else
        {
            sliceBlock2 = new();
        }
        sliceBlock2.AddStorageRead(TestItem.AddressB, 1);
        sliceBlock2.AddStorageRead(TestItem.AddressB, 2);
        sliceBlock2.AddStorageRead(TestItem.AddressB, 3);
        sliceBlock2.AddStorageRead(TestItem.AddressB, 4);
        bal.TrackAddedEntries(sliceBlock2.Log, sliceBlock2.Start);

        Assert.That(bal.ItemCount, Is.EqualTo(5),
            "block 2: 1 account + 4 storage reads, no carryover from block 1");
    }

    public static IEnumerable<TestCaseData> CountNewItemsCases()
    {
        yield return new TestCaseData(
            (Action<BlockAccessIndexRecorder>)(static s => s.AddAccountRead(TestItem.AddressA)),
            0).SetName("CountNewItems_existing_account_read_adds_nothing");
        yield return new TestCaseData(
            (Action<BlockAccessIndexRecorder>)(static s => s.AddAccountRead(TestItem.AddressC)),
            1).SetName("CountNewItems_new_account_adds_one");
        yield return new TestCaseData(
            (Action<BlockAccessIndexRecorder>)(static s =>
            {
                s.AddStorageRead(TestItem.AddressA, 1);
                s.AddStorageRead(TestItem.AddressA, 2);
                s.AddStorageChange(TestItem.AddressB, 7, before: UInt256.Zero, after: UInt256.One);
            }),
            0).SetName("CountNewItems_existing_keys_add_nothing_whether_read_or_written");
        yield return new TestCaseData(
            (Action<BlockAccessIndexRecorder>)(static s =>
            {
                s.AddStorageRead(TestItem.AddressA, 3);
                s.AddStorageChange(TestItem.AddressA, 4, before: UInt256.Zero, after: UInt256.One);
                s.AddStorageRead(TestItem.AddressA, 4);
            }),
            2).SetName("CountNewItems_new_keys_of_existing_account_count_once_each");
        yield return new TestCaseData(
            (Action<BlockAccessIndexRecorder>)(static s =>
            {
                s.AddStorageChange(TestItem.AddressC, 1, before: UInt256.Zero, after: UInt256.One);
                s.AddStorageRead(TestItem.AddressC, 1);
                s.AddStorageRead(TestItem.AddressC, 2);
                s.AddBalanceChange(TestItem.AddressD, before: UInt256.Zero, after: UInt256.One);
            }),
            4).SetName("CountNewItems_new_accounts_count_with_their_keys");
    }

    [TestCaseSource(nameof(CountNewItemsCases))]
    public void CountNewItems_matches_the_item_count_a_merge_adds(Action<BlockAccessIndexRecorder> mutate, int expected)
    {
        BlockAccessIndexRecorder first = new();
        first.AddStorageRead(TestItem.AddressA, 1);
        first.AddStorageChange(TestItem.AddressA, 2, before: UInt256.Zero, after: UInt256.One);
        first.AddStorageRead(TestItem.AddressB, 7);
        BlockAccessListItemSet bal = new();
        bal.TrackAddedEntries(first.Log, first.Start);
        int before = bal.ItemCount;

        BlockAccessIndexRecorder next = new();
        mutate(next);
        int counted = bal.CountNewItems(next.Log, next.Start);
        bal.TrackAddedEntries(next.Log, next.Start);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(counted, Is.EqualTo(expected));
            Assert.That(bal.ItemCount - before, Is.EqualTo(counted));
        }
    }

    [Test]
    public void Reset_trims_oversized_account_dictionary()
    {
        const int OversizedCapacity = CoreCollectionExtensions.DefaultTrimAboveCapacity + 1;

        BlockAccessListItemSet bal = new();
        object accountChanges = typeof(BlockAccessListItemSet).GetField(
            "_accounts",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(bal)!;
        MethodInfo ensureCapacity = accountChanges.GetType().GetMethod(
            nameof(HashSet<>.EnsureCapacity),
            [typeof(int)])!;
        PropertyInfo capacity = accountChanges.GetType().GetProperty(nameof(HashSet<>.Capacity))!;
        ensureCapacity.Invoke(accountChanges, [OversizedCapacity]);
        int capacityBeforeReset = (int)capacity.GetValue(accountChanges)!;

        bal.Clear();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(capacityBeforeReset, Is.GreaterThan(CoreCollectionExtensions.DefaultTrimAboveCapacity));
            Assert.That(capacity.GetValue(accountChanges), Is.GreaterThan(0));
            Assert.That(capacity.GetValue(accountChanges), Is.LessThan(capacityBeforeReset));
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
}
