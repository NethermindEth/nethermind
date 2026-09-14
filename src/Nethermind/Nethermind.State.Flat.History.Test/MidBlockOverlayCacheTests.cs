// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
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
        using (MidBlockOverlayCache.Lease lease = _cache.Rent(Block, 2))
        {
            first = lease.Overlay;
        }

        using MidBlockOverlayCache.Lease second = _cache.Rent(Block, 5);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(second.Overlay, Is.SameAs(first), "tracing a block transaction by transaction must fold each changeset once, not once per transaction");
            Assert.That(second.Overlay.Folded, Is.EqualTo(5));
            Assert.That(BalanceOf(second.Overlay), Is.EqualTo((UInt256)5));
        }
    }

    [Test]
    public void AnOverlayAnotherRequestStillHolds_IsNotExtendedUnderIt()
    {
        using MidBlockOverlayCache.Lease held = _cache.Rent(Block, 2);
        using MidBlockOverlayCache.Lease other = _cache.Rent(Block, 6);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(other.Overlay, Is.Not.SameAs(held.Overlay));
            Assert.That(held.Overlay.Folded, Is.EqualTo(2), "the holder is reading the state before transaction 2 and must not see what a later transaction wrote");
            Assert.That(other.Overlay.Folded, Is.EqualTo(6));
        }
    }

    [Test]
    public void AnEarlierTransactionOfTheSameBlock_FoldsTheBlockAgain()
    {
        using (_cache.Rent(Block, 6))
        {
        }

        using MidBlockOverlayCache.Lease earlier = _cache.Rent(Block, 1);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(earlier.Overlay.Folded, Is.EqualTo(1));
            Assert.That(BalanceOf(earlier.Overlay), Is.EqualTo((UInt256)1));
        }
    }

    [Test]
    public void TheFirstTransactionOfABlock_SeesNothing()
    {
        using MidBlockOverlayCache.Lease lease = _cache.Rent(Block, 0);

        Assert.That(lease.Overlay.AccountCount, Is.Zero);
    }

    private static UInt256? BalanceOf(MidBlockOverlay overlay)
    {
        overlay.TryGetAccount(TestItem.AddressA, out MidBlockOverlay.AccountOverlay account);
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
