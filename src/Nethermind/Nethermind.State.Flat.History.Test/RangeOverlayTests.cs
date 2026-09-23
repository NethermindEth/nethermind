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
    private static readonly Account Parent = new(5, 100, TestItem.KeccakA, TestItem.KeccakB);
    private static readonly StorageCell SlotOne = new(TestItem.AddressA, 1);
    private static readonly StorageCell SlotTwo = new(TestItem.AddressA, 2);
    private static readonly HashSet<AddressAsKey> None = [];

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
            Assert.That(account!.StorageRoot, Is.Not.EqualTo(Keccak.EmptyTreeHash),
                "the block wrote a slot after its own wipe, so the account holds storage again");
            Assert.That(account.CodeHash, Is.EqualTo(Keccak.Compute(Code)));
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

        later.TryGetAccount(TestItem.AddressA, Parent, out Account? account);

        Assert.That(account!.StorageRoot, Is.EqualTo(Keccak.EmptyTreeHash),
            "the slot the older block wrote went with the wipe; only a write in the wiping block or after it leaves the account holding storage");
    }

    [Test]
    public void AnAccountRecreatedWithStorageAfterAnOlderBlockWipedIt_DoesNotReportAnEmptyRoot()
    {
        RangeOverlay destroyed = Chain(null, 7, c => c.Deleted(TestItem.AddressA));
        RangeOverlay recreated = Chain(destroyed, 8, c =>
        {
            c.Balance(TestItem.AddressA, 3);
            c.Storage(SlotOne, [0x44]);
        });
        RangeOverlay later = Chain(recreated, 9, c => c.Balance(TestItem.AddressB, 1));

        later.TryGetAccount(TestItem.AddressA, Parent, out Account? account);

        Assert.That(account!.StorageRoot, Is.Not.EqualTo(Keccak.EmptyTreeHash),
            "a wipe in an older block says nothing once a newer one wrote slots again; reported empty, a CREATE over this account later in the run would skip its wipe and read these slots back instead of zero");
    }

    [Test]
    public void AnAddressOneBlockRefuses_IsRefusedByTheWholeChain()
    {
        RangeOverlay wrote = Chain(null, 7, c =>
        {
            c.Balance(TestItem.AddressA, 10);
            c.Storage(SlotOne, [0x11]);
        });
        RangeOverlay refused = Chain(wrote, 8, c =>
        {
            c.Balance(TestItem.AddressA, 20);
            c.Storage(SlotOne, [0x22]);
        }, [TestItem.AddressA]);
        RangeOverlay later = Chain(refused, 9, c => c.Balance(TestItem.AddressB, 1));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(refused.TryGetAccount(TestItem.AddressA, Parent, out _), Is.False, "the withdrawal or system call that refused it is not in the rows, so only the parent state knows the account");
            Assert.That(refused.TryGetStorage(TestItem.AddressA, 1, out _), Is.False, "the older block still holds the slot, and answering it would serve a value the refusing block has since changed");
            Assert.That(refused.HasStorage(TestItem.AddressA), Is.False);
            Assert.That(later.TryGetAccount(TestItem.AddressA, Parent, out _), Is.False, "a chain built on the refusal carries it");
            Assert.That(later.TryGetStorage(TestItem.AddressA, 1, out _), Is.False);
            Assert.That(later.TryGetAccount(TestItem.AddressB, Parent, out Account? b), Is.True, "everything else is still answered");
            Assert.That(b!.Balance, Is.EqualTo(UInt256.One));
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
            Assert.That(afterSeven.Entries, Is.EqualTo(1), "one account written");
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
        capture.Tracer.StartNewTxTrace(block.Transactions[0]).ReportBalanceChange(TestItem.AddressA, 1, 2);
        capture.Tracer.EndTxTrace();
        capture.Tracer.EndBlockTrace();
        capture.Commit();
        Assert.That(BlockChangesets.TryRead(new TransactionChangesetStore(columns.GetColumnDb(FlatHistoryColumns.TransactionChangesets)), number, hash, 1, out BlockChangesets? rows), Is.True);
        return rows!;
    }
}
