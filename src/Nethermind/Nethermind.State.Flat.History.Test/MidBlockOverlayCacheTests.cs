// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.State.Flat.History.Changesets;
using NUnit.Framework;

namespace Nethermind.State.Flat.History.Test;

public class MidBlockOverlayCacheTests
{
    private const ulong Block = 100;

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
    public void TracingTheNextTransactionOfABlock_ExtendsTheOverlayInsteadOfRebuildingIt()
    {
        MidBlockOverlay first;
        Assert.That(_cache.TryRent(Block, 2, out MidBlockOverlayCache.Lease lease), Is.True);
        using (lease)
        {
            first = lease.Overlay;
        }

        _cache.TryRent(Block, 5, out MidBlockOverlayCache.Lease second);
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
        _cache.TryRent(Block, 2, out MidBlockOverlayCache.Lease held);
        _cache.TryRent(Block, 6, out MidBlockOverlayCache.Lease other);
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
        _cache.TryRent(Block, 6, out MidBlockOverlayCache.Lease later);
        later.Dispose();

        _cache.TryRent(Block, 1, out MidBlockOverlayCache.Lease earlier);
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
        _cache.TryRent(Block, 0, out MidBlockOverlayCache.Lease lease);
        using (lease)
        {
            Assert.That(lease.Overlay.AccountCount, Is.Zero);
        }
    }

    [Test]
    public void APrefixTheRowsDoNotReach_IsNotLent() =>
        Assert.That(_cache.TryRent(Block, 12, out _), Is.False, "eight transactions are written; a twelfth cannot be seeded from them and must be replayed");

    [Test]
    public void APrefixWithARowMissing_IsNotLent()
    {
        Span<byte> key = stackalloc byte[ChangesetKeyLayout.RowKeyLength];
        ChangesetKeyLayout.WriteRowKey(key, Block, 3);
        _columns.GetColumnDb(FlatHistoryColumns.TransactionChangesets).Remove(key);

        Assert.That(_cache.TryRent(Block, 6, out _), Is.False, "a gap in the prefix would be folded over silently and change the target's state");
    }

    private static UInt256? BalanceOf(MidBlockOverlay overlay)
    {
        overlay.TryGetAccount(TestItem.AddressA, out MidBlockOverlay.AccountOverlay? account);
        return account?.Balance;
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
