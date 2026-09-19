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
        Assert.That(_cache.TryRent(Block, in HashA, 2, out MidBlockOverlayCache.Lease lease), Is.True);
        using (lease)
        {
            first = lease.Overlay;
        }

        _cache.TryRent(Block, in HashA, 5, out MidBlockOverlayCache.Lease second);
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
        _cache.TryRent(Block, in HashA, 2, out MidBlockOverlayCache.Lease held);
        _cache.TryRent(Block, in HashA, 6, out MidBlockOverlayCache.Lease other);
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
        _cache.TryRent(Block, in HashA, 6, out MidBlockOverlayCache.Lease later);
        later.Dispose();

        _cache.TryRent(Block, in HashA, 1, out MidBlockOverlayCache.Lease earlier);
        using (earlier)
        {

            using (Assert.EnterMultipleScope())
            {
                Assert.That(earlier.Overlay.Folded, Is.EqualTo(1));
                Assert.That(BalanceOf(earlier.Overlay), Is.EqualTo((UInt256)1));
            }
        }
    }

    [Test]
    public void TheFirstTransactionOfABlock_SeesNothing()
    {
        _cache.TryRent(Block, in HashA, 0, out MidBlockOverlayCache.Lease lease);
        using (lease)
        {
            Assert.That(lease.Overlay.TryGetAccount(TestItem.AddressA, out _), Is.False);
        }
    }

    [Test]
    public void APrefixTheRowsDoNotReach_IsNotLent() =>
        Assert.That(_cache.TryRent(Block, in HashA, 12, out _), Is.False, "eight transactions are written; a twelfth cannot be seeded from them and must be replayed");

    [Test]
    public void APrefixWithARowMissing_IsNotLent()
    {
        Span<byte> key = stackalloc byte[ChangesetKeyLayout.RowKeyLength];
        ChangesetKeyLayout.WriteRowKey(key, Block, 3);
        _columns.GetColumnDb(FlatHistoryColumns.TransactionChangesets).Remove(key);

        Assert.That(_cache.TryRent(Block, in HashA, 6, out _), Is.False, "a gap in the prefix would be folded over silently and change the target's state");
    }

    [Test]
    public void ARowTheCodecCannotRead_IsRefused_AndLeavesNothingOfItselfBehind()
    {
        // The row holds an account entry the fold applies and a storage entry it cannot read, so the refusal happens
        // with part of transaction 3's own writes already in the overlay.
        WriteHalfReadableRow(3, 99);

        bool past = _cache.TryRent(Block, in HashA, 6, out MidBlockOverlayCache.Lease pastLease);
        bool atTheBoundary = _cache.TryRent(Block, in HashA, 3, out MidBlockOverlayCache.Lease boundaryLease);

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
        MidBlockOverlay folded;
        Assert.That(_cache.TryRent(Block, in HashA, 5, out MidBlockOverlayCache.Lease first), Is.True);
        using (first)
        {
            folded = first.Overlay;
        }

        Assert.That(_cache.TryRent(Block, in HashB, 5, out MidBlockOverlayCache.Lease second), Is.True);
        using (second)
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(second.Overlay, Is.Not.SameAs(folded), "a height is not an identity: the sibling gets its own fold, not the one already in the cache");
                Assert.That(second.Overlay.Hash, Is.EqualTo(HashB));
            }
        }
    }

    private void WriteBalance(ushort transactionIndex, UInt256 balance)
    {
        ChangesetCollector collector = new();
        collector.Balance(TestItem.AddressA, balance);
        using (IColumnsWriteBatch<FlatHistoryColumns> batch = _columns.StartWriteBatch())
        {
            _store.Write(Block, transactionIndex, collector.Pack(), batch.GetColumnBatch(FlatHistoryColumns.TransactionChangesets));
        }

        collector.Release();
    }
}
