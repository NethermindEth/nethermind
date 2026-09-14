// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.State.Flat.History.Changesets;
using NUnit.Framework;

namespace Nethermind.State.Flat.History.Test;

public class TransactionChangesetStoreTests
{
    private SnapshotableMemColumnsDb<FlatHistoryColumns> _columns = null!;
    private TransactionChangesetStore _store = null!;

    [SetUp]
    public void SetUp()
    {
        _columns = new SnapshotableMemColumnsDb<FlatHistoryColumns>();
        _store = new TransactionChangesetStore(_columns.GetColumnDb(FlatHistoryColumns.TransactionChangesets));
    }

    [TearDown]
    public void TearDown() => _columns.Dispose();

    [Test]
    public void TheWritesBeforeATransaction_AreOneRangeInExecutionOrder()
    {
        WriteTransaction(block: 10, transactionIndex: 0, TestItem.AddressA);
        WriteTransaction(block: 10, transactionIndex: 1, TestItem.AddressB);
        WriteTransaction(block: 10, transactionIndex: 2, TestItem.AddressC);
        WriteTransaction(block: 11, transactionIndex: 0, TestItem.AddressD);

        List<(ulong Block, ushort Index)> seen = [];
        using (ISortedView view = _store.OpenBefore(block: 10, beforeTransaction: 2))
        {
            while (view.MoveNext()) seen.Add((ChangesetKeyLayout.BlockOf(view.CurrentKey), ChangesetKeyLayout.TransactionIndexOf(view.CurrentKey)));
        }

        Assert.That(seen, Is.EqualTo(new[] { (10UL, (ushort)0), (10UL, (ushort)1) }),
            "the scan must stop before the traced transaction and must not cross into the next block");
    }

    [Test]
    public void Coverage_ExtendsOnlyWhenTheNewRangeTouchesIt()
    {
        bool first = _store.TryExtendCoverage(100, 200);
        bool adjacent = _store.TryExtendCoverage(201, 300);
        bool disjoint = _store.TryExtendCoverage(500, 600);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(first, Is.True);
            Assert.That(adjacent, Is.True);
            Assert.That(disjoint, Is.False, "coverage is one contiguous range; claiming a disjoint one would report a gap as indexed");
            Assert.That(_store.TryGetCoverage(out ulong from, out ulong to) && from == 100 && to == 300, Is.True);
            Assert.That(_store.Covers(250), Is.True);
            Assert.That(_store.Covers(550), Is.False);
        }
    }

    [Test]
    public void CoverageIsAbsent_OnAFreshColumn() =>
        Assert.That(_store.TryGetCoverage(out _, out _), Is.False);

    private void WriteTransaction(ulong block, ushort transactionIndex, Address address)
    {
        byte[] buffer = new byte[ChangesetCodec.MaxAccountEntryLength];
        int length = ChangesetCodec.WriteAccount(buffer, address, [0x01], [], [], deleted: false, storageCleared: false);
        using IColumnsWriteBatch<FlatHistoryColumns> batch = _columns.StartWriteBatch();
        _store.Write(block, transactionIndex, buffer.AsSpan(0, length), batch.GetColumnBatch(FlatHistoryColumns.TransactionChangesets));
    }
}
