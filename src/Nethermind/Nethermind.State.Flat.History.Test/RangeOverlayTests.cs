// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.State.Flat.History.Changesets;
using NUnit.Framework;

namespace Nethermind.State.Flat.History.Test;

public class RangeOverlayTests
{
    private static readonly byte[] Code = [0x60, 0x00, 0x55];
    private static readonly StorageCell SlotOne = new(TestItem.AddressA, 1);
    private static readonly StorageCell SlotTwo = new(TestItem.AddressA, 2);
    private static readonly HashSet<AddressAsKey> None = [];

    [Test]
    public void TheNewestBlockAnswersFirst_AndAnOlderBlockFillsWhatItLeftAlone()
    {
        RangeOverlay older = Chain(null, 7, c =>
        {
            c.Storage(SlotOne, [0x11]);
            c.Storage(SlotTwo, [0x99]);
        });
        RangeOverlay chain = Chain(older, 8, c => c.Storage(SlotTwo, [0x22]));

        chain.TryGetStorage(TestItem.AddressA, 1, out UInt256 one);
        chain.TryGetStorage(TestItem.AddressA, 2, out UInt256 two);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(one, Is.EqualTo((UInt256)0x11), "the older block, since the newer left the slot alone");
            Assert.That(two, Is.EqualTo((UInt256)0x22), "newest block");
            Assert.That(chain.TryGetStorage(TestItem.AddressA, 3, out _), Is.False, "a slot no block wrote belongs to the parent state");
            Assert.That(chain.Length, Is.EqualTo(2));
        }
    }

    [Test]
    public void AnAccountWrite_HoldsNoEntry()
    {
        RangeOverlay chain = Chain(null, 7, c =>
        {
            c.Balance(TestItem.AddressA, 10);
            c.Nonce(TestItem.AddressA, 1);
            c.Code(TestItem.AddressA, Code);
        });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(chain.Entries, Is.Zero, "the trace reads every account from the parent state, so the chain keeps none");
            Assert.That(chain.HasStorage(TestItem.AddressA), Is.False);
        }
    }

    [Test]
    public void ADestroyedAccount_ReadsItsSlotsAsZero_UntilABlockWritesThemAgain()
    {
        RangeOverlay written = Chain(null, 7, c => c.Storage(SlotOne, [0x11]));
        RangeOverlay destroyed = Chain(written, 8, c => c.Deleted(TestItem.AddressA));
        destroyed.TryGetStorage(TestItem.AddressA, 1, out UInt256 afterDestroy);

        RangeOverlay recreated = Chain(destroyed, 9, c =>
        {
            c.Balance(TestItem.AddressA, 7);
            c.Storage(SlotTwo, [0x44]);
        });
        recreated.TryGetStorage(TestItem.AddressA, 1, out UInt256 untouched);
        recreated.TryGetStorage(TestItem.AddressA, 2, out UInt256 rewritten);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(afterDestroy, Is.EqualTo(UInt256.Zero), "the slot the earlier block wrote went with the account");
            Assert.That(destroyed.HasStorage(TestItem.AddressA), Is.True, "the wipe must reach the overlay even where the parent account holds no storage");
            Assert.That(untouched, Is.EqualTo(UInt256.Zero), "the recreating block did not bring the old slot back");
            Assert.That(rewritten, Is.EqualTo((UInt256)0x44));
        }
    }

    [Test]
    public void AWipeInANewerBlock_HidesSlotsAnOlderBlockWrote_ButNotSlotsWrittenAfterIt()
    {
        RangeOverlay older = Chain(null, 7, c =>
        {
            c.Storage(SlotOne, [0x11]);
            c.Storage(SlotTwo, [0x22]);
        });
        RangeOverlay chain = Chain(older, 8, c =>
        {
            c.StorageCleared(TestItem.AddressA);
            c.Code(TestItem.AddressA, Code);
            c.Storage(SlotTwo, [0x33]);
        });

        chain.TryGetStorage(TestItem.AddressA, 1, out UInt256 one);
        chain.TryGetStorage(TestItem.AddressA, 2, out UInt256 two);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(one, Is.EqualTo(UInt256.Zero));
            Assert.That(two, Is.EqualTo((UInt256)0x33));
        }
    }

    [Test]
    public void AWipeInANewerBlock_IsNotUndoneBySlotsAnOlderBlockWrote()
    {
        RangeOverlay written = Chain(null, 7, c => c.Storage(SlotOne, [0x11]));
        RangeOverlay wiped = Chain(written, 8, c =>
        {
            c.StorageCleared(TestItem.AddressA);
            c.Code(TestItem.AddressA, Code);
        });
        RangeOverlay later = Chain(wiped, 9, c => c.Balance(TestItem.AddressB, 1));

        bool known = later.TryGetStorage(TestItem.AddressA, 1, out UInt256 value);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(known, Is.True, "the wipe answers the slot; the parent state is not asked");
            Assert.That(value, Is.EqualTo(UInt256.Zero), "the slot the older block wrote went with the wipe");
        }
    }

    [Test]
    public void AnAddressOneBlockRefuses_IsRefusedByTheWholeChain()
    {
        StorageCell slotOfB = new(TestItem.AddressB, 1);
        RangeOverlay wrote = Chain(null, 7, c => c.Storage(SlotOne, [0x11]));
        RangeOverlay refused = Chain(wrote, 8, c => c.Storage(SlotOne, [0x22]), [TestItem.AddressA]);
        RangeOverlay later = Chain(refused, 9, c => c.Storage(slotOfB, [0x01]));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(refused.TryGetStorage(TestItem.AddressA, 1, out _), Is.False, "the older block still holds the slot, and answering it would serve a value the refusing block has since changed");
            Assert.That(refused.HasStorage(TestItem.AddressA), Is.False);
            Assert.That(later.TryGetStorage(TestItem.AddressA, 1, out _), Is.False, "a chain built on the refusal carries it");
            Assert.That(later.TryGetStorage(TestItem.AddressB, 1, out UInt256 b), Is.True, "everything else is still answered");
            Assert.That(b, Is.EqualTo(UInt256.One));
        }
    }

    [Test]
    public void ABlockMostlyRefused_ReservesCapacityOnlyForTheWritesItKeeps()
    {
        const int refusedCount = 100;
        HashSet<AddressAsKey> refused = [];
        for (int i = 0; i < refusedCount; i++) refused.Add(TestItem.Addresses[i]);

        RangeOverlay chain = Chain(null, 7, c =>
        {
            for (int i = 0; i < refusedCount; i++) c.Storage(new StorageCell(TestItem.Addresses[i], 1), [0x11]);
            c.Storage(new StorageCell(TestItem.Addresses[refusedCount], 1), [0x22]);
        }, refused);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(chain.Entries, Is.EqualTo(refusedCount + 1), "the refusal and the one kept slot");
            Assert.That(chain.Capacity, Is.LessThan(refusedCount), "a refused write reserves no slot or storage account");
            Assert.That(chain.TryGetStorage(TestItem.Addresses[refusedCount], 1, out UInt256 kept), Is.True);
            Assert.That(kept, Is.EqualTo((UInt256)0x22));
        }
    }

    [Test]
    public void AChainThatHoldsTooManyEntries_IsNotExtended()
    {
        ConsecutiveBlockOverlays overlays = new(maxEntries: 1);
        BlockChangesets seven = Rows(7, TestItem.KeccakA);
        BlockChangesets eight = Rows(8, TestItem.KeccakB);

        overlays.Publish(seven, null, None);
        RangeOverlay afterSeven = overlays.EndingAt(7, TestItem.KeccakA)!;
        overlays.Publish(eight, afterSeven, None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(afterSeven.Entries, Is.EqualTo(1), "one slot written");
            Assert.That(overlays.EndingAt(8, TestItem.KeccakB)!.Length, Is.EqualTo(1), "the budget is spent, so the block starts a new chain");
        }
    }

    [Test]
    public void ConsecutiveBlockOverlays_ChainOntoTheBlockTheyContinue_AndCutAtTheLimit()
    {
        ConsecutiveBlockOverlays overlays = new(maxBlocks: 2);
        BlockChangesets seven = Rows(7, TestItem.KeccakA);
        BlockChangesets eight = Rows(8, TestItem.KeccakB);
        BlockChangesets nine = Rows(9, TestItem.KeccakC);

        overlays.Publish(seven, null, None);
        RangeOverlay? afterSeven = overlays.EndingAt(7, TestItem.KeccakA);
        overlays.Publish(eight, afterSeven, None);
        RangeOverlay? afterEight = overlays.EndingAt(8, TestItem.KeccakB);
        overlays.Publish(nine, afterEight, None);
        RangeOverlay? afterNine = overlays.EndingAt(9, TestItem.KeccakC);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(afterSeven!.Length, Is.EqualTo(1));
            Assert.That(afterEight!.Length, Is.EqualTo(2));
            Assert.That(afterNine!.Length, Is.EqualTo(1), "a chain at the limit is not extended; the block starts a new one");
            Assert.That(overlays.EndingAt(8, TestItem.KeccakD), Is.Null, "the hash must match: a sibling at that height continues nothing");
        }
    }

    private static RangeOverlay Chain(RangeOverlay? older, ulong block, Action<ChangesetCollector> writes, HashSet<AddressAsKey>? excluded = null)
    {
        MidBlockOverlay overlay = new();
        overlay.Reset(block);
        ChangesetCollector collector = new();
        writes(collector);
        overlay.Fold(0, collector.Pack());
        collector.Release();
        return RangeOverlay.Extend(older, overlay, Keccak.Compute(block.ToString()), excluded ?? None);
    }

    private static BlockChangesets Rows(ulong number, Hash256 hash)
    {
        using SnapshotableMemColumnsDb<FlatHistoryColumns> columns = new();
        TransactionChangesetIndex index = new(columns, new FlatDbConfig { HistoryTransactionIndexEnabled = true });
        Block block = Build.A.Block.WithNumber(number).WithTransactions(Build.A.Transaction.TestObject).TestObject;
        block.Header.Hash = hash;
        using TransactionChangesetIndex.BlockCapture capture = index.StartBlock(number);
        capture.Tracer.StartNewBlockTrace(block);
        capture.Tracer.StartNewTxTrace(block.Transactions[0]).ReportStorageChange(SlotOne, [0x01], [0x02]);
        capture.Tracer.EndTxTrace();
        capture.Tracer.EndBlockTrace();
        capture.Commit();
        Assert.That(BlockChangesets.TryRead(new TransactionChangesetStore(columns.GetColumnDb(FlatHistoryColumns.TransactionChangesets)), number, hash, 1, out BlockChangesets? rows), Is.True);
        return rows!;
    }
}
