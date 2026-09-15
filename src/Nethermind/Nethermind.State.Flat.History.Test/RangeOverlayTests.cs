// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
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
    private static readonly Account Parent = new(5, 100, TestItem.KeccakA, TestItem.KeccakB);
    private static readonly StorageCell SlotOne = new(TestItem.AddressA, 1);
    private static readonly StorageCell SlotTwo = new(TestItem.AddressA, 2);

    [Test]
    public void TheNewestBlockAnswersFirst_AndAnOlderBlockFillsWhatItLeftAlone()
    {
        RangeOverlay older = Chain(null, 7, c =>
        {
            c.Balance(TestItem.AddressA, 10);
            c.Nonce(TestItem.AddressA, 1);
            c.Storage(SlotOne, [0x11]);
        });
        RangeOverlay chain = Chain(older, 8, c =>
        {
            c.Balance(TestItem.AddressA, 20);
            c.Storage(SlotTwo, [0x22]);
        });

        chain.TryGetAccount(TestItem.AddressA, Parent, out Account? account);
        chain.TryGetStorage(TestItem.AddressA, 1, out UInt256 one);
        chain.TryGetStorage(TestItem.AddressA, 2, out UInt256 two);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(account!.Balance, Is.EqualTo((UInt256)20), "newest block");
            Assert.That(account.Nonce, Is.EqualTo(1UL), "the older block, since the newer left the nonce alone");
            Assert.That(account.CodeHash, Is.EqualTo(TestItem.KeccakB), "neither block: the parent's");
            Assert.That(one, Is.EqualTo((UInt256)0x11));
            Assert.That(two, Is.EqualTo((UInt256)0x22));
            Assert.That(chain.TryGetStorage(TestItem.AddressA, 3, out _), Is.False, "a slot no block wrote belongs to the parent state");
            Assert.That(chain.Length, Is.EqualTo(2));
        }
    }

    [Test]
    public void ADestroyedAccount_IsGone_AndItsSlotsReadZero_UntilABlockRecreatesIt()
    {
        RangeOverlay written = Chain(null, 7, c => c.Storage(SlotOne, [0x11]));
        RangeOverlay destroyed = Chain(written, 8, c => c.Deleted(TestItem.AddressA));

        bool goneKnown = destroyed.TryGetAccount(TestItem.AddressA, Parent, out Account? gone);
        destroyed.TryGetStorage(TestItem.AddressA, 1, out UInt256 afterDestroy);

        RangeOverlay recreated = Chain(destroyed, 9, c => c.Balance(TestItem.AddressA, 7));
        recreated.TryGetAccount(TestItem.AddressA, Parent, out Account? fresh);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(goneKnown, Is.True);
            Assert.That(gone, Is.Null, "the account does not exist after the block that destroyed it");
            Assert.That(afterDestroy, Is.EqualTo(UInt256.Zero), "the slot the earlier block wrote went with the account");
            Assert.That(destroyed.HasStorage(TestItem.AddressA), Is.True);
            Assert.That(fresh!.Balance, Is.EqualTo((UInt256)7));
            Assert.That(fresh.Nonce, Is.EqualTo(0UL), "not the parent's five: the account was destroyed in between");
            Assert.That(fresh.StorageRoot, Is.EqualTo(Keccak.EmptyTreeHash));
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
        chain.TryGetAccount(TestItem.AddressA, Parent, out Account? account);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(one, Is.EqualTo(UInt256.Zero));
            Assert.That(two, Is.EqualTo((UInt256)0x33));
            Assert.That(account!.StorageRoot, Is.EqualTo(Keccak.EmptyTreeHash));
            Assert.That(account.CodeHash, Is.EqualTo(Keccak.Compute(Code)));
        }
    }

    [Test]
    public void ConsecutiveBlockOverlays_ChainOntoTheBlockTheyContinue_AndCutAtTheLimit()
    {
        ConsecutiveBlockOverlays overlays = new(maxBlocks: 2);
        BlockChangesets seven = Rows(7, TestItem.KeccakA);
        BlockChangesets eight = Rows(8, TestItem.KeccakB);
        BlockChangesets nine = Rows(9, TestItem.KeccakC);

        overlays.Publish(seven, null);
        RangeOverlay? afterSeven = overlays.EndingAt(7, TestItem.KeccakA);
        overlays.Publish(eight, afterSeven);
        RangeOverlay? afterEight = overlays.EndingAt(8, TestItem.KeccakB);
        overlays.Publish(nine, afterEight);
        RangeOverlay? afterNine = overlays.EndingAt(9, TestItem.KeccakC);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(afterSeven!.Length, Is.EqualTo(1));
            Assert.That(afterEight!.Length, Is.EqualTo(2));
            Assert.That(afterNine!.Length, Is.EqualTo(1), "a chain at the limit is not extended; the block starts a new one");
            Assert.That(overlays.EndingAt(8, TestItem.KeccakD), Is.Null, "the hash must match: a sibling at that height continues nothing");
        }
    }

    private static RangeOverlay Chain(RangeOverlay? older, ulong block, Action<ChangesetCollector> writes)
    {
        MidBlockOverlay overlay = new();
        overlay.Reset(block);
        ChangesetCollector collector = new();
        writes(collector);
        overlay.Fold(0, collector.Pack());
        collector.Release();
        return RangeOverlay.Extend(older, overlay, Keccak.Compute(block.ToString()));
    }

    private static BlockChangesets Rows(ulong number, Hash256 hash)
    {
        using SnapshotableMemColumnsDb<FlatHistoryColumns> columns = new();
        TransactionChangesetIndex index = new(columns, new FlatDbConfig { HistoryTransactionIndexEnabled = true });
        Block block = Build.A.Block.WithNumber(number).WithTransactions(Build.A.Transaction.TestObject).TestObject;
        block.Header.Hash = hash;
        using TransactionChangesetIndex.BlockCapture capture = index.StartBlock(number);
        capture.Tracer.StartNewBlockTrace(block);
        capture.Tracer.StartNewTxTrace(block.Transactions[0]).ReportBalanceChange(TestItem.AddressA, 1, 2);
        capture.Tracer.EndTxTrace();
        capture.Tracer.EndBlockTrace();
        capture.Commit();
        Assert.That(BlockChangesets.TryRead(new TransactionChangesetStore(columns.GetColumnDb(FlatHistoryColumns.TransactionChangesets)), number, hash, 1, out BlockChangesets? rows), Is.True);
        return rows!;
    }
}
