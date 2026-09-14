// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Evm.Tracing;
using Nethermind.State.Flat.History.Changesets;
using NUnit.Framework;

namespace Nethermind.State.Flat.History.Test;

public class TransactionChangesetIndexTests
{
    private SnapshotableMemColumnsDb<FlatHistoryColumns> _columns = null!;
    private TransactionChangesetIndex _index = null!;
    private Block _block = null!;

    [SetUp]
    public void SetUp()
    {
        _columns = new SnapshotableMemColumnsDb<FlatHistoryColumns>();
        _index = new TransactionChangesetIndex(_columns, new FlatDbConfig { HistoryTransactionIndexEnabled = true });
        _block = Build.A.Block.WithNumber(7)
            .WithTransactions(Build.A.Transaction.WithNonce(0).TestObject, Build.A.Transaction.WithNonce(1).TestObject, Build.A.Transaction.WithNonce(2).TestObject)
            .TestObject;
    }

    [TearDown]
    public void TearDown() => _columns.Dispose();

    [Test]
    public void TheBlockTheRowsWereBuiltFrom_IsServed()
    {
        Capture(transactions: 3);

        bool rented = _index.TryRentOverlay(7, _block.Hash!, 2, out MidBlockOverlayCache.Lease lease);
        using (lease)
        {
            Assert.That(rented, Is.True);
        }
    }

    [Test]
    public void ABlockThatOnlySharesTheHeight_IsRefused()
    {
        Capture(transactions: 3);
        Block sibling = Build.A.Block.WithNumber(7).WithDifficulty(99).WithTransactions(_block.Transactions).TestObject;

        Assert.That(_index.TryRentOverlay(7, sibling.Hash!, 2, out _), Is.False,
            "a reorged sibling or a caller-supplied body must not be traced from the canonical block's prefix");
    }

    [Test]
    public void ACaptureThatDidNotSeeTheWholeBlock_ClaimsNothing()
    {
        bool committed = Capture(transactions: 2);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(committed, Is.False);
            Assert.That(_index.Covers(7), Is.False, "rows for part of a block must never be claimed as the block");
        }
    }

    [Test]
    public void ACaptureWhereATransactionReportedNothing_ClaimsNothing()
    {
        bool committed = Capture(transactions: 3, silent: 1);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(committed, Is.False);
            Assert.That(_index.Covers(7), Is.False, "every transaction changes its sender's nonce, so silence means the execution was not observed");
        }
    }

    private bool Capture(int transactions, int silent = -1)
    {
        using TransactionChangesetIndex.BlockCapture capture = _index.StartBlock(7);
        capture.Tracer.StartNewBlockTrace(_block);
        for (int i = 0; i < transactions; i++)
        {
            ITxTracer tracer = capture.Tracer.StartNewTxTrace(_block.Transactions[i]);
            if (i != silent) tracer.ReportBalanceChange(TestItem.AddressA, (ulong)i, (ulong)(i + 1));
            capture.Tracer.EndTxTrace();
        }

        capture.Tracer.EndBlockTrace();
        return capture.Commit() && _index.TryClaim(7, 7);
    }
}
