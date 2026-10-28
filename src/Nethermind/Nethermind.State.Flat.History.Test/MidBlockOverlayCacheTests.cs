// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.State.Flat.History.Changesets;
using NUnit.Framework;

namespace Nethermind.State.Flat.History.Test;

public class MidBlockOverlayCacheTests
{
    private const ulong Block = 100;

    private static readonly ValueHash256 HashA = TestItem.KeccakA;
    private static readonly ValueHash256 HashB = TestItem.KeccakB;

    private SnapshotableMemColumnsDb<FlatHistoryColumns> _columns = null!;
    private TransactionChangesetStore _store = null!;
    private MidBlockOverlayCache _cache = null!;

    [SetUp]
    public void SetUp()
    {
        _columns = new SnapshotableMemColumnsDb<FlatHistoryColumns>();
        _store = new TransactionChangesetStore(_columns.GetColumnDb(FlatHistoryColumns.TransactionChangesets));
        _cache = new MidBlockOverlayCache(_store);

        for (ushort transaction = 0; transaction < 8; transaction++) WriteBalance(transaction, (UInt256)(transaction + 1));
    }

    [TearDown]
    public void TearDown() => _columns.Dispose();

    [Test]
    public void TryRent_WhenGenerationChanges_DiscardsThePreviousFold()
    {
        Assert.That(_cache.TryRent(Block, in HashA, 2, out MidBlockOverlayCache.Lease first, version: 1), Is.True);
        using (first)
        {
            WriteBalance(1, 99);
            Assert.That(_cache.TryRent(Block, in HashA, 2, out MidBlockOverlayCache.Lease second, version: 2), Is.True);
            using (second)
            {
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(BalanceOf(first.Overlay), Is.EqualTo((UInt256)2), "existing readers retain their immutable prefix");
                    Assert.That(BalanceOf(second.Overlay), Is.EqualTo((UInt256)99), "a fold from an invalidated version must not be reused");
                }
            }
        }
    }

    [Test]
    public void TracingTheNextTransactionOfABlock_ExtendsTheOverlayInsteadOfRebuildingIt()
    {
        MidBlockOverlay first;
        Assert.That(_cache.TryRent(Block, in HashA, 2, out MidBlockOverlayCache.Lease lease, version: 0), Is.True);
        using (lease)
        {
            first = lease.Overlay;
        }

        _cache.TryRent(Block, in HashA, 5, out MidBlockOverlayCache.Lease second, version: 0);
        using (second)
        {

            using (Assert.EnterMultipleScope())
            {
                Assert.That(second.Overlay, Is.SameAs(first), "tracing a block transaction by transaction must fold each changeset once, not once per transaction");
                Assert.That(second.Overlay.Folded, Is.EqualTo(5));
                Assert.That(BalanceOf(second.Overlay), Is.EqualTo((UInt256)5));
            }
        }
    }

    [Test]
    public void AnOverlayAnotherRequestStillHolds_IsNotExtendedUnderIt()
    {
        _cache.TryRent(Block, in HashA, 2, out MidBlockOverlayCache.Lease held, version: 0);
        _cache.TryRent(Block, in HashA, 6, out MidBlockOverlayCache.Lease other, version: 0);
        using (held)
        using (other)
        {

            using (Assert.EnterMultipleScope())
            {
                Assert.That(other.Overlay, Is.Not.SameAs(held.Overlay));
                Assert.That(held.Overlay.Folded, Is.EqualTo(2), "the holder is reading the state before transaction 2 and must not see what a later transaction wrote");
                Assert.That(other.Overlay.Folded, Is.EqualTo(6));
            }
        }
    }

    [Test]
    public void AnEarlierTransactionOfTheSameBlock_FoldsTheBlockAgain()
    {
        _cache.TryRent(Block, in HashA, 6, out MidBlockOverlayCache.Lease later, version: 0);
        MidBlockOverlay folded = later.Overlay;
        later.Dispose();

        _cache.TryRent(Block, in HashA, 1, out MidBlockOverlayCache.Lease earlier, version: 0);
        using (earlier)
        {

            using (Assert.EnterMultipleScope())
            {
                Assert.That(earlier.Overlay, Is.SameAs(folded), "no request holds the later prefix, so its overlay is folded again");
                Assert.That(earlier.Overlay.Folded, Is.EqualTo(1));
                Assert.That(BalanceOf(earlier.Overlay), Is.EqualTo((UInt256)1));
            }
        }
    }

    [Test]
    public void AnEarlierTransactionOfTheSameBlock_LeavesALaterPrefixARequestHolds()
    {
        _cache.TryRent(Block, in HashA, 6, out MidBlockOverlayCache.Lease later, version: 0);
        using (later)
        {
            _cache.TryRent(Block, in HashA, 1, out MidBlockOverlayCache.Lease earlier, version: 0);
            using (earlier)
            {
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(earlier.Overlay, Is.Not.SameAs(later.Overlay));
                    Assert.That(later.Overlay.Folded, Is.EqualTo(6), "the holder is reading the state before transaction 6");
                    Assert.That(earlier.Overlay.Folded, Is.EqualTo(1));
                    Assert.That(BalanceOf(earlier.Overlay), Is.EqualTo((UInt256)1));
                }
            }
        }
    }

    [Test]
    public void TheFirstTransactionOfABlock_SeesNothing()
    {
        _cache.TryRent(Block, in HashA, 0, out MidBlockOverlayCache.Lease lease, version: 0);
        using (lease)
        {
            Assert.That(lease.Overlay.TryGetAccount(TestItem.AddressA, out _), Is.False);
        }
    }

    [Test]
    public void APrefixTheRowsDoNotReach_IsNotLent() =>
        Assert.That(_cache.TryRent(Block, in HashA, 12, out _, version: 0), Is.False, "eight transactions are written; a twelfth cannot be seeded from them and must be replayed");

    [Test]
    public void APrefixWithARowMissing_IsNotLent()
    {
        Span<byte> key = stackalloc byte[ChangesetKeyLayout.RowKeyLength];
        ChangesetKeyLayout.WriteRowKey(key, Block, 3);
        _columns.GetColumnDb(FlatHistoryColumns.TransactionChangesets).Remove(key);

        Assert.That(_cache.TryRent(Block, in HashA, 6, out _, version: 0), Is.False, "a gap in the prefix would be folded over silently and change the target's state");
    }

    [Test]
    public void ARowTheCodecCannotRead_IsRefused_AndLeavesNothingOfItselfBehind()
    {
        // The row holds an account entry the fold applies and a storage entry it cannot read, so the refusal happens
        // with part of transaction 3's own writes already in the overlay.
        WriteHalfReadableRow(3, 99);

        bool past = _cache.TryRent(Block, in HashA, 6, out MidBlockOverlayCache.Lease pastLease, version: 0);
        bool atTheBoundary = _cache.TryRent(Block, in HashA, 3, out MidBlockOverlayCache.Lease boundaryLease, version: 0);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(past, Is.False, "a row the codec cannot read is a refusal, and the trace replays the prefix");
            Assert.That(atTheBoundary, Is.True, "the transactions before the unreadable one are still readable, so a trace of it is still seeded");
            Assert.That(BalanceOf(boundaryLease.Overlay), Is.EqualTo((UInt256)3),
                "what the refused fold had already applied is transaction 3's own writes, and lending them would seed the target with itself");
        }

        if (past) pastLease.Dispose();
        if (atTheBoundary) boundaryLease.Dispose();
    }

    [Test]
    public void AFoldThatFailsForAnyOtherReason_IsRefused_AndTheEntryIsLentAgainAfterwards()
    {
        using FailingColumn column = new();
        TransactionChangesetStore store = new(column);
        MidBlockOverlayCache cache = new(store);
        for (ushort transaction = 0; transaction < 4; transaction++) WriteBalance(store, column, transaction, (UInt256)(transaction + 1));

        column.Failure = new IOException("column read failed");
        bool refused = !cache.TryRent(Block, in HashA, 3, out _, version: 0);
        column.Failure = null;
        bool rented = cache.TryRent(Block, in HashA, 3, out MidBlockOverlayCache.Lease lease, version: 0);

        using (lease)
        using (Assert.EnterMultipleScope())
        {
            Assert.That(refused, Is.True, "a read that fails is a refusal like any other: the trace replays the prefix rather than fail the request");
            Assert.That(rented, Is.True, "the pin and the extending flag went back with the refusal, so the entry is not dead until the next rent replaces it");
            Assert.That(BalanceOf(lease.Overlay), Is.EqualTo((UInt256)3));
        }
    }

    private void WriteHalfReadableRow(ushort transactionIndex, UInt256 balance)
    {
        ChangesetCollector collector = new();
        collector.Balance(TestItem.AddressA, balance);
        collector.Storage(new StorageCell(TestItem.AddressA, 1), [0x11]);
        byte[] packed = collector.Pack().ToArray();
        collector.Release();

        using IColumnsWriteBatch<FlatHistoryColumns> batch = _columns.StartWriteBatch();
        _store.Write(Block, transactionIndex, packed.AsSpan(0, packed.Length - 1), batch.GetColumnBatch(FlatHistoryColumns.TransactionChangesets));
    }

    private static UInt256? BalanceOf(MidBlockOverlay overlay)
    {
        overlay.TryGetAccount(TestItem.AddressA, out MidBlockOverlay.AccountOverlay? account);
        return account?.Balance;
    }

    [Test]
    public void ASiblingIndexedAtTheSameHeight_IsNotLentTheOtherBlocksPrefix()
    {
        Assert.That(_cache.TryRent(Block, in HashA, 5, out MidBlockOverlayCache.Lease first, version: 0), Is.True);
        using (first)
        {
            Assert.That(_cache.TryRent(Block, in HashB, 5, out MidBlockOverlayCache.Lease second, version: 0), Is.True);
            using (second)
            {
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(second.Overlay, Is.Not.SameAs(first.Overlay), "a height is not an identity: the sibling gets its own fold, not the one already in the cache");
                    Assert.That(first.Overlay.Hash, Is.EqualTo(HashA));
                    Assert.That(second.Overlay.Hash, Is.EqualTo(HashB));
                }
            }
        }
    }

    [Test]
    public void ASiblingIndexedAtTheSameHeight_RefoldsAnOverlayNobodyHolds()
    {
        Assert.That(_cache.TryRent(Block, in HashA, 5, out MidBlockOverlayCache.Lease first, version: 0), Is.True);
        MidBlockOverlay folded = first.Overlay;
        first.Dispose();

        Assert.That(_cache.TryRent(Block, in HashB, 3, out MidBlockOverlayCache.Lease second, version: 0), Is.True);
        using (second)
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(second.Overlay, Is.SameAs(folded));
                Assert.That(second.Overlay.Hash, Is.EqualTo(HashB), "the overlay was reset for the sibling, not lent with the prefix of the other block");
                Assert.That(second.Overlay.Folded, Is.EqualTo(3));
                Assert.That(BalanceOf(second.Overlay), Is.EqualTo((UInt256)3));
            }
        }
    }

    [Test]
    public void AnEvictedOverlayNobodyHolds_IsFoldedAgainForTheNewBlock()
    {
        MidBlockOverlayCache cache = new(_store, blocks: 2);
        IDb column = _columns.GetColumnDb(FlatHistoryColumns.TransactionChangesets);
        for (ushort transaction = 0; transaction < 4; transaction++)
        {
            WriteBalance(_store, column, Block + 1, transaction, (UInt256)(transaction + 101));
            WriteBalance(_store, column, Block + 2, transaction, (UInt256)(transaction + 201));
        }

        MidBlockOverlay evicted;
        Assert.That(cache.TryRent(Block, in HashA, 6, out MidBlockOverlayCache.Lease first, version: 0), Is.True);
        using (first)
        {
            evicted = first.Overlay;
        }

        Assert.That(cache.TryRent(Block + 1, in HashA, 3, out MidBlockOverlayCache.Lease second, version: 0), Is.True);
        second.Dispose();
        Assert.That(cache.TryRent(Block + 2, in HashB, 2, out MidBlockOverlayCache.Lease third, version: 0), Is.True);
        using (third)
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(third.Overlay, Is.SameAs(evicted), "the least recently rented block made room, and nobody held its overlay");
                Assert.That(third.Overlay.Block, Is.EqualTo(Block + 2));
                Assert.That(third.Overlay.Hash, Is.EqualTo(HashB));
                Assert.That(third.Overlay.Folded, Is.EqualTo(2));
                Assert.That(BalanceOf(third.Overlay), Is.EqualTo((UInt256)202), "nothing of the six transactions folded for the evicted block remains");
            }
        }

        Assert.That(cache.TryRent(Block + 1, in HashA, 3, out MidBlockOverlayCache.Lease kept, version: 0), Is.True);
        using (kept)
        {
            Assert.That(BalanceOf(kept.Overlay), Is.EqualTo((UInt256)103), "the block rented more recently stayed cached");
        }
    }

    [Test]
    public void AnEvictedOverlayARequestStillHolds_IsLeftToIt()
    {
        MidBlockOverlayCache cache = new(_store, blocks: 1);
        IDb column = _columns.GetColumnDb(FlatHistoryColumns.TransactionChangesets);
        for (ushort transaction = 0; transaction < 4; transaction++) WriteBalance(_store, column, Block + 1, transaction, (UInt256)(transaction + 101));

        Assert.That(cache.TryRent(Block, in HashA, 5, out MidBlockOverlayCache.Lease held, version: 0), Is.True);
        using (held)
        {
            Assert.That(cache.TryRent(Block + 1, in HashA, 3, out MidBlockOverlayCache.Lease other, version: 0), Is.True);
            using (other)
            {
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(other.Overlay, Is.Not.SameAs(held.Overlay));
                    Assert.That(held.Overlay.Block, Is.EqualTo(Block));
                    Assert.That(held.Overlay.Folded, Is.EqualTo(5));
                    Assert.That(BalanceOf(held.Overlay), Is.EqualTo((UInt256)5), "the holder is still reading the evicted block");
                    Assert.That(BalanceOf(other.Overlay), Is.EqualTo((UInt256)103));
                }
            }
        }
    }

    private void WriteBalance(ushort transactionIndex, UInt256 balance) =>
        WriteBalance(_store, _columns.GetColumnDb(FlatHistoryColumns.TransactionChangesets), transactionIndex, balance);

    private static void WriteBalance(TransactionChangesetStore store, IDb column, ushort transactionIndex, UInt256 balance) =>
        WriteBalance(store, column, Block, transactionIndex, balance);

    private static void WriteBalance(TransactionChangesetStore store, IDb column, ulong block, ushort transactionIndex, UInt256 balance)
    {
        ChangesetCollector collector = new();
        collector.Balance(TestItem.AddressA, balance);
        using (IWriteBatch batch = column.StartWriteBatch())
        {
            store.Write(block, transactionIndex, collector.Pack(), batch);
        }

        collector.Release();
    }

    /// <summary>A sorted column whose range reads fail on demand, the way a closing or damaged store would.</summary>
    private sealed class FailingColumn : TestMemDb, ISortedKeyValueStore
    {
        public Exception? Failure { get; set; }

        public new ISortedView GetViewBetween(ReadOnlySpan<byte> firstKeyInclusive, ReadOnlySpan<byte> lastKeyExclusive, ReadFlags flags = ReadFlags.None)
        {
            if (Failure is { } failure) throw failure;

            return base.GetViewBetween(firstKeyInclusive, lastKeyExclusive, flags);
        }
    }
}
