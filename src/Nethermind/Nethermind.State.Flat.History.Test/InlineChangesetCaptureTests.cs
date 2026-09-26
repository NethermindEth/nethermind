// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
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
    private InlineChangesetCapture _capture = null!;
    private Block _block = null!;
    private bool _syncing;

    [SetUp]
    public void SetUp()
    {
        _columns = new SnapshotableMemColumnsDb<FlatHistoryColumns>();
        _index = new TransactionChangesetIndex(_columns, new FlatDbConfig { HistoryTransactionIndexEnabled = true });
        // One capture for the whole test, as in production: it is a singleton on the processing path, so anything
        // it keeps between blocks is kept across every block the node executes.
        _capture = new InlineChangesetCapture(_index, new AlwaysCapture(() => _syncing), LimboLogs.Instance);
        _block = Build.A.Block.WithNumber(9).WithTransactions(Build.A.Transaction.WithNonce(0).TestObject, Build.A.Transaction.WithNonce(1).TestObject).TestObject;
        _syncing = true;
    }

    [TearDown]
    public void TearDown() => _columns.Dispose();

    [Test]
    public void ABlockExecutedWhileSyncing_WritesItsRowsAndLeavesTheClaimToTheBuilder()
    {
        Process(observe: [true, true]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_index.HasRowsOf(9, _block.Hash!), Is.True);
            Assert.That(_index.Covers(9), Is.False,
                "capture runs before the block is validated, so a rejected block must not leave its height covered");
        }
    }

    [Test]
    public void RowsWrittenInline_SeedTheOverlayOnceTheHeightIsClaimed()
    {
        Process(observe: [true, true]);
        _index.TryClaim(9, 9);

        bool rented = _index.TryRentOverlay(9, _block.Hash!, 2, out MidBlockOverlayCache.Lease lease);
        using (lease)
        {
            lease.Overlay.TryGetAccount(TestItem.AddressA, out MidBlockOverlay.AccountOverlay? account);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(rented, Is.True);
                Assert.That(account!.Nonce, Is.EqualTo((UInt256)2), "the second transaction's write is what the overlay ends on");
            }
        }
    }

    [Test]
    public void EmptyBlock_StillWritesItsRows()
    {
        _block = Build.A.Block.WithNumber(9).WithTransactions([]).TestObject;

        Process(observe: []);

        Assert.That(_index.HasRowsOf(9, _block.Hash!), Is.True,
            "an empty block must not leave a gap that prevents subsequent inline coverage");
    }

    [Test]
    public void ABlockNearTheTip_IsLeftToTheBuilder()
    {
        _syncing = false;

        Process(observe: [true, true]);

        Assert.That(_index.HasRowsOf(9, _block.Hash!), Is.False, "near the tip a reorg can still remove the block; only durable history is indexed there");
    }

    [Test]
    public void ABlockWhoseExecutionWasNotObserved_WritesNothing()
    {
        Process(observe: [true, false]);

        Assert.That(_index.HasRowsOf(9, _block.Hash!), Is.False, "a transaction that reported no change was executed where the tracer cannot see, so the rows would be wrong");
    }

    [Test]
    public void ABlockCarryingATransactionThatIsNotItsOwn_WritesNothing()
    {
        _capture.StartNewBlockTrace(_block);
        Observe(_capture.StartNewTxTrace(_block.Transactions[0]), 0);
        _capture.EndTxTrace();

        // A system transaction injected by a plugin: traced, but with nowhere to record its writes.
        ITxTracer stray = _capture.StartNewTxTrace(Build.A.Transaction.WithNonce(7).TestObject);
        _capture.EndTxTrace();
        Observe(_capture.StartNewTxTrace(_block.Transactions[1]), 1);
        _capture.EndTxTrace();
        _capture.EndBlockTrace();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(stray.IsTracingState, Is.False, "a transaction the block does not hold has no position to record against");
            Assert.That(_index.HasRowsOf(9, _block.Hash!), Is.False,
                "its writes went nowhere, so a later transaction seeded from this prefix would be missing them");
        }
    }

    [Test]
    public void ABlockReusingItsCollectors_CarriesNothingOverFromTheBlockBefore()
    {
        Process(observe: [true, true]);
        _block = Build.A.Block.WithNumber(10).WithTransactions(Build.A.Transaction.WithNonce(0).TestObject).TestObject;
        Process(observe: [true], address: TestItem.AddressB);
        _index.TryClaim(10, 10);

        bool rented = _index.TryRentOverlay(10, _block.Hash!, 1, out MidBlockOverlayCache.Lease lease);
        using (lease)
        {
            bool carriedOver = lease.Overlay.TryGetAccount(TestItem.AddressA, out _);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(rented, Is.True);
                Assert.That(carriedOver, Is.False, "collectors are kept across blocks, so a stale write must not reach the next block's rows");
            }
        }
    }

    private void Process(bool[] observe, Address? address = null)
    {
        _capture.StartNewBlockTrace(_block);
        for (int i = 0; i < _block.Transactions.Length; i++)
        {
            ITxTracer tracer = _capture.StartNewTxTrace(_block.Transactions[i]);
            if (observe[i]) Observe(tracer, i, address);
            _capture.EndTxTrace();
        }

        _capture.EndBlockTrace();
    }

    private static void Observe(ITxTracer tracer, int index, Address? address = null)
    {
        if (tracer.IsTracingState) tracer.ReportNonceChange(address ?? TestItem.AddressA, (UInt256)index, (UInt256)(index + 1));
    }

    private sealed class AlwaysCapture(Func<bool>? predicate = null) : IInlineCapturePolicy
    {
        public bool ShouldCapture(Block block) => predicate?.Invoke() ?? true;
    }
}
