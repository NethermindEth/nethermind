// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using NUnit.Framework;

namespace Nethermind.Core.Test.BlockAccessLists;

/// <summary>
/// Regression tests for BlockAccessList.ItemCount cache invalidation. The cached
/// _itemCount must not survive a structural mutation: BlockAccessListManager reuses
/// the same generated BAL instance across blocks, so a stale count would let an
/// oversized BAL pass — or reject a valid one — in BlockValidator's EIP-7928 size check.
/// </summary>
[TestFixture]
public class BlockAccessListItemCountTests
{
    [Test]
    public void ItemCount_recomputes_after_Reset()
    {
        BlockAccessList bal = new();
        bal.AddAccountRead(TestItem.AddressA);
        bal.AddAccountRead(TestItem.AddressB);
        Assert.That(bal.ItemCount, Is.EqualTo(2), "initial population");

        bal.Reset();
        bal.AddAccountRead(TestItem.AddressC);

        Assert.That(bal.ItemCount, Is.EqualTo(1));
    }

    [Test]
    public void ItemCount_recomputes_after_Clear()
    {
        BlockAccessList bal = new();
        bal.AddAccountRead(TestItem.AddressA);
        bal.AddStorageRead(TestItem.AddressA, 1);
        Assert.That(bal.ItemCount, Is.EqualTo(2));

        bal.Clear();
        bal.AddAccountRead(TestItem.AddressB);

        Assert.That(bal.ItemCount, Is.EqualTo(1));
    }

    public static IEnumerable<TestCaseData> MutatorCases()
    {
        yield return new TestCaseData(
            (Action<BlockAccessList>)(static bal => bal.AddAccountRead(TestItem.AddressB)), 2)
            .SetName("ItemCount_recomputes_after_AddAccountRead");
        yield return new TestCaseData(
            (Action<BlockAccessList>)(static bal => bal.AddStorageRead(TestItem.AddressA, 1)), 2)
            .SetName("ItemCount_recomputes_after_AddStorageRead");
        yield return new TestCaseData(
            (Action<BlockAccessList>)(static bal => bal.AddStorageChange(TestItem.AddressA, 1, before: 0, after: 42)), 2)
            .SetName("ItemCount_recomputes_after_AddStorageChange");
        yield return new TestCaseData(
            (Action<BlockAccessList>)(static bal => bal.AddBalanceChange(TestItem.AddressB, before: UInt256.Zero, after: (UInt256)100)), 2)
            .SetName("ItemCount_recomputes_after_AddBalanceChange");
        yield return new TestCaseData(
            (Action<BlockAccessList>)(static bal => bal.AddNonceChange(TestItem.AddressB, 1)), 2)
            .SetName("ItemCount_recomputes_after_AddNonceChange");
        yield return new TestCaseData(
            (Action<BlockAccessList>)(static bal => bal.AddCodeChange(TestItem.AddressB, before: Array.Empty<byte>(), after: new byte[] { 0x60 })), 2)
            .SetName("ItemCount_recomputes_after_AddCodeChange");
    }

    [TestCaseSource(nameof(MutatorCases))]
    public void ItemCount_recomputes_after_mutation(Action<BlockAccessList> mutate, int expected)
    {
        BlockAccessList bal = new();
        bal.AddAccountRead(TestItem.AddressA);
        Assert.That(bal.ItemCount, Is.EqualTo(1), "cache established");

        mutate(bal);

        Assert.That(bal.ItemCount, Is.EqualTo(expected));
    }

    [Test]
    public void ItemCount_recomputes_after_Merge()
    {
        BlockAccessList target = new();
        target.AddAccountRead(TestItem.AddressA);
        Assert.That(target.ItemCount, Is.EqualTo(1), "cache established");

        BlockAccessList other = new();
        other.AddAccountRead(TestItem.AddressB);
        other.AddStorageRead(TestItem.AddressB, 7);

        target.Merge(other);

        Assert.That(target.ItemCount, Is.EqualTo(3));
    }

    [Test]
    public void ItemCount_reflects_reuse_across_blocks_through_Reset()
    {
        BlockAccessList bal = new();
        bal.AddAccountRead(TestItem.AddressA);
        bal.AddStorageRead(TestItem.AddressA, 1);
        bal.AddStorageRead(TestItem.AddressA, 2);
        Assert.That(bal.ItemCount, Is.EqualTo(3), "block 1 cached count");

        bal.Reset();
        bal.AddAccountRead(TestItem.AddressB);
        bal.AddStorageRead(TestItem.AddressB, 1);
        bal.AddStorageRead(TestItem.AddressB, 2);
        bal.AddStorageRead(TestItem.AddressB, 3);
        bal.AddStorageRead(TestItem.AddressB, 4);

        Assert.That(bal.ItemCount, Is.EqualTo(5),
            "block 2 must report fresh count, not the stale block-1 cache");
    }
}
