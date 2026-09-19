// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State.Flat.History.Changesets;
using NUnit.Framework;

namespace Nethermind.State.Flat.History.Test;

public class InlineChangesetCaptureTests
{
    private SnapshotableMemColumnsDb<FlatHistoryColumns> _columns = null!;
    private TransactionChangesetIndex _index = null!;
    private Block _block = null!;
    private bool _syncing;

    [SetUp]
    public void SetUp()
    {
        _columns = new SnapshotableMemColumnsDb<FlatHistoryColumns>();
        _index = new TransactionChangesetIndex(_columns, new FlatDbConfig { HistoryTransactionIndexEnabled = true });
        _block = Build.A.Block.WithNumber(9).WithTransactions(Build.A.Transaction.WithNonce(0).TestObject, Build.A.Transaction.WithNonce(1).TestObject).TestObject;
        _syncing = true;
    }

    [TearDown]
    public void TearDown() => _columns.Dispose();

    [Test]
    public void ABlockExecutedWhileSyncing_IsIndexedAndClaimed()
    {
        Process(observe: [true, true]);

        bool rented = _index.TryRentOverlay(9, _block.Hash!, 2, out MidBlockOverlayCache.Lease lease);
        using (lease)
        {
            lease.Overlay.TryGetAccount(TestItem.AddressA, out MidBlockOverlay.AccountOverlay? account);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(_index.Covers(9), Is.True);
                Assert.That(rented, Is.True);
                Assert.That(account!.Nonce, Is.EqualTo((UInt256)2), "the second transaction's write is what the overlay ends on");
            }
        }
    }

    [Test]
    public void EmptyBlock_BetweenCoveredBlocks_ExtendsCoverage()
    {
        _index.TryClaim(8, 8);
        _block = Build.A.Block.WithNumber(9).WithTransactions([]).TestObject;
        Process(observe: []);
        _block = Build.A.Block.WithNumber(10).WithTransactions(Build.A.Transaction.TestObject).TestObject;
        Process(observe: [true]);

        Assert.That(_index.TryGetCoverage(out ulong from, out ulong to) && from == 8 && to == 10, Is.True,
            "an empty block must not leave a gap that prevents subsequent inline coverage");
    }

    [Test]
    public void ABlockNearTheTip_IsLeftToTheBuilder()
    {
        _syncing = false;

        Process(observe: [true, true]);

        Assert.That(_index.Covers(9), Is.False, "near the tip a reorg can still remove the block; only durable history is indexed there");
    }

    [Test]
    public void ABlockWhoseExecutionWasNotObserved_IsNotClaimed()
    {
        Process(observe: [true, false]);

        Assert.That(_index.Covers(9), Is.False, "a transaction that reported no change was executed where the tracer cannot see, so the rows would be wrong");
    }

    [Test]
    public void ABlockThatDoesNotTouchCoverage_WritesRowsButClaimsNothing()
    {
        _index.TryClaim(3, 3);

        Process(observe: [true, true]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_index.Covers(9), Is.False, "coverage is one contiguous range; the builder fills the gap and claims it then");
            Assert.That(_index.TryGetCoverage(out _, out ulong to) && to == 3, Is.True);
        }
    }

    private void Process(bool[] observe)
    {
        InlineChangesetCapture capture = new(_index, _ => _syncing, LimboLogs.Instance);
        capture.StartNewBlockTrace(_block);
        for (int i = 0; i < _block.Transactions.Length; i++)
        {
            ITxTracer tracer = capture.StartNewTxTrace(_block.Transactions[i]);
            if (observe[i] && tracer.IsTracingState) tracer.ReportNonceChange(TestItem.AddressA, (UInt256)i, (UInt256)(i + 1));
            capture.EndTxTrace();
        }

        capture.EndBlockTrace();
    }
}
