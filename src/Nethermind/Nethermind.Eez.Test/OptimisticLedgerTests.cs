// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Eez.Sequencer;
using NUnit.Framework;

namespace Nethermind.Eez.Test;

public class OptimisticLedgerTests
{
    private OptimisticLedger _ledger = null!;

    [SetUp]
    public void SetUp() => _ledger = new OptimisticLedger();

    [Test]
    public void Blocks_BatchAboveTheCursorSettledByItsObserver_StillBlocks()
    {
        _ledger.Begin(Batch(12));
        _ledger.MarkSettled(12);

        Assert.That(_ledger.Blocks(6), Is.True, "only the cursor passing the batch lets the next one start from it");
    }

    [Test]
    public void ConfirmThrough_CursorPassesTheBatch_Unblocks()
    {
        _ledger.Begin(Batch(12));

        _ledger.ConfirmThrough(12);

        Assert.That(_ledger.Blocks(12), Is.False, "a batch the cursor settled is settled whatever its observer said");
        Assert.That(_ledger.TakeFailed(12), Is.Null, "and it is not recovered");
    }

    [Test]
    public void TakeFailed_FailedBatchAboveTheCursor_IsTakenOnce()
    {
        _ledger.Begin(Batch(12));
        _ledger.MarkFailed(12, slotSkipped: true);

        (PostedBatch Batch, bool SlotSkipped)? failed = _ledger.TakeFailed(6);

        Assert.That((failed?.Batch.SyncHeight, failed?.SlotSkipped), Is.EqualTo(((ulong?)12, (bool?)true)), "the failed batch and why it failed");
        Assert.That(_ledger.TakeFailed(6), Is.Null, "a batch is recovered once");
        Assert.That(_ledger.Blocks(6), Is.False, "recovery reopens the gate");
    }

    [Test]
    public void MarkFailed_AfterSettled_KeepsTheVerdict()
    {
        _ledger.Begin(Batch(12));
        _ledger.MarkSettled(12);

        _ledger.MarkFailed(12, slotSkipped: false);

        Assert.That(_ledger.TakeFailed(6), Is.Null, "a late failure report never overrides a settlement");
    }

    [TestCase(true, false, TestName = "SettledByItsObserver")]
    [TestCase(false, true, TestName = "StillPending")]
    public void RollBack_L1ReorganizedTheCursorBelowABatch_ForgetsItOnlyIfSettled(bool settled, bool blocks)
    {
        _ledger.Begin(Batch(12));
        if (settled)
        {
            _ledger.MarkSettled(12);
        }

        _ledger.RollBack(6);

        Assert.That(_ledger.Blocks(6), Is.EqualTo(blocks), "a settled batch the reorganization took out is posted again; a pending one is left to its observer");
    }

    private static PostedBatch Batch(ulong height) =>
        new(height, Keccak.Compute($"postBatch {height}"), Build.A.BlockHeader.WithNumber(height - 1).TestObject);
}
