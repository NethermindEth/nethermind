// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using NUnit.Framework;

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
        BlockAccessListAtIndex slice = new() { Index = 0 };
        slice.AddAccountRead(TestItem.AddressA);

        GeneratedBlockAccessList bal = new();
        bal.Merge(slice);

        Assert.That(bal.ItemCount, Is.EqualTo(1));
    }

    public static IEnumerable<TestCaseData> SingleMutationCases()
    {
        // Item count is 1 per account + 1 per storage change + 1 per storage read (matches
        // ReadOnlyBlockAccessList.ItemCount and what BlockValidator.ValidateBlockLevelAccessListSize consumes).
        yield return new TestCaseData(
            (Action<BlockAccessListAtIndex>)(static s => s.AddBalanceChange(TestItem.AddressA, before: UInt256.Zero, after: (UInt256)100)),
            1).SetName("ItemCount_balance_change_counts_only_account");
        yield return new TestCaseData(
            (Action<BlockAccessListAtIndex>)(static s => s.AddNonceChange(TestItem.AddressA, 1)),
            1).SetName("ItemCount_nonce_change_counts_only_account");
        yield return new TestCaseData(
            (Action<BlockAccessListAtIndex>)(static s => s.AddCodeChange(TestItem.AddressA, before: Array.Empty<byte>(), after: new byte[] { 0x60 })),
            1).SetName("ItemCount_code_change_counts_only_account");
        yield return new TestCaseData(
            (Action<BlockAccessListAtIndex>)(static s => s.AddStorageChange(TestItem.AddressA, 1, before: UInt256.Zero, after: (UInt256)42)),
            2).SetName("ItemCount_storage_change_counts_account_plus_slot");
        yield return new TestCaseData(
            (Action<BlockAccessListAtIndex>)(static s => s.AddStorageRead(TestItem.AddressA, 1)),
            2).SetName("ItemCount_storage_read_counts_account_plus_slot");
    }

    [TestCaseSource(nameof(SingleMutationCases))]
    public void ItemCount_after_single_mutation(Action<BlockAccessListAtIndex> mutate, int expected)
    {
        BlockAccessListAtIndex slice = new() { Index = 0 };
        mutate(slice);

        GeneratedBlockAccessList bal = new();
        bal.Merge(slice);

        Assert.That(bal.ItemCount, Is.EqualTo(expected));
    }

    [Test]
    public void ItemCount_combines_storage_changes_and_reads_across_accounts()
    {
        BlockAccessListAtIndex slice = new() { Index = 0 };
        slice.AddStorageChange(TestItem.AddressA, 1, before: UInt256.Zero, after: (UInt256)42);
        slice.AddStorageRead(TestItem.AddressA, 2);
        slice.AddBalanceChange(TestItem.AddressB, before: UInt256.Zero, after: (UInt256)1);
        slice.AddStorageRead(TestItem.AddressB, 7);

        GeneratedBlockAccessList bal = new();
        bal.Merge(slice);

        // A: account + storage change + storage read = 3; B: account + storage read = 2.
        Assert.That(bal.ItemCount, Is.EqualTo(5));
    }

    /// <summary>
    /// BAL instances are reused across blocks via <see cref="GeneratedBlockAccessList.Reset"/>;
    /// the next block's <c>ItemCount</c> must report a fresh count, never the stale prior one.
    /// </summary>
    [Test]
    public void ItemCount_reflects_reuse_across_blocks_through_Reset()
    {
        BlockAccessListAtIndex sliceBlock1 = new() { Index = 0 };
        sliceBlock1.AddStorageRead(TestItem.AddressA, 1);
        sliceBlock1.AddStorageRead(TestItem.AddressA, 2);

        GeneratedBlockAccessList bal = new();
        bal.Merge(sliceBlock1);
        Assert.That(bal.ItemCount, Is.EqualTo(3), "block 1: 1 account + 2 storage reads");

        bal.Reset();

        BlockAccessListAtIndex sliceBlock2 = new() { Index = 0 };
        sliceBlock2.AddStorageRead(TestItem.AddressB, 1);
        sliceBlock2.AddStorageRead(TestItem.AddressB, 2);
        sliceBlock2.AddStorageRead(TestItem.AddressB, 3);
        sliceBlock2.AddStorageRead(TestItem.AddressB, 4);
        bal.Merge(sliceBlock2);

        Assert.That(bal.ItemCount, Is.EqualTo(5),
            "block 2: 1 account + 4 storage reads, no carryover from block 1");
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
