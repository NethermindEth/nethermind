// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Logging;
using Nethermind.TxPool;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Merge.Plugin.Test;

[TestFixture]
[Parallelizable(ParallelScope.Self)]
public class FrameTxWidthFinalizerTests
{
    private const ulong GasUsed = 21_000;

    [Test]
    public void Finalized_block_earns_width_from_its_receipts_once()
    {
        Block finalized = FrameBlock(number: 5);
        (IBlockTree blockTree, IReceiptFinder receiptFinder, IFrameTxWidthLedger ledger, ITxPool txPool) = Wire(finalized);
        FrameTxWidthFinalizer finalizer = new(blockTree, receiptFinder, txPool, Enabled(), LimboLogs.Instance);

        blockTree.BlocksFinalized += Raise.EventWith(new FinalizeEventArgs(finalized.Header));

        using (Assert.EnterMultipleScope())
        {
            ledger.Received(1).EarnWidthOnFinalization(finalized, Arg.Any<TxReceipt[]>());
            receiptFinder.Received(1).Get(finalized, Arg.Any<bool>(), false);
        }
        finalizer.Dispose();
    }

    [Test]
    public void Failure_mid_gap_resumes_without_crediting_a_block_twice()
    {
        Block first = FrameBlock(number: 5);
        Block gap = FrameBlock(number: 6);
        Block last = FrameBlock(number: 7);
        (IBlockTree blockTree, IReceiptFinder receiptFinder, IFrameTxWidthLedger ledger, ITxPool txPool) = Wire(first, gap, last);
        int lastAttempts = 0;
        ledger.When(l => l.EarnWidthOnFinalization(last, Arg.Any<TxReceipt[]>()))
            .Do(_ => { if (++lastAttempts == 1) throw new InvalidOperationException(); });
        FrameTxWidthFinalizer finalizer = new(blockTree, receiptFinder, txPool, Enabled(), LimboLogs.Instance);

        blockTree.BlocksFinalized += Raise.EventWith(new FinalizeEventArgs(first.Header));
        blockTree.BlocksFinalized += Raise.EventWith(new FinalizeEventArgs(last.Header));
        blockTree.BlocksFinalized += Raise.EventWith(new FinalizeEventArgs(last.Header));

        using (Assert.EnterMultipleScope())
        {
            ledger.Received(1).EarnWidthOnFinalization(first, Arg.Any<TxReceipt[]>());
            ledger.Received(1).EarnWidthOnFinalization(gap, Arg.Any<TxReceipt[]>());
            ledger.Received(2).EarnWidthOnFinalization(last, Arg.Any<TxReceipt[]>());
        }
        finalizer.Dispose();
    }

    [Test]
    public void Reorged_out_block_earns_nothing()
    {
        Block canonical = FrameBlock(number: 5);
        Block reorgedOut = FrameBlock(number: 5);
        (IBlockTree blockTree, IReceiptFinder receiptFinder, IFrameTxWidthLedger ledger, ITxPool txPool) = Wire(canonical);
        FrameTxWidthFinalizer finalizer = new(blockTree, receiptFinder, txPool, Enabled(), LimboLogs.Instance);

        blockTree.BlocksFinalized += Raise.EventWith(new FinalizeEventArgs(canonical.Header));

        ledger.Received(1).EarnWidthOnFinalization(canonical, Arg.Any<TxReceipt[]>());
        ledger.DidNotReceive().EarnWidthOnFinalization(reorgedOut, Arg.Any<TxReceipt[]>());
        finalizer.Dispose();
    }

    [Test]
    public void Disabled_width_never_earns_on_finalization()
    {
        Block finalized = FrameBlock(number: 5);
        (IBlockTree blockTree, IReceiptFinder receiptFinder, IFrameTxWidthLedger ledger, ITxPool txPool) = Wire(finalized);
        FrameTxWidthFinalizer finalizer = new(blockTree, receiptFinder, txPool, new TxPoolConfig { FrameTxWidthEnabled = false }, LimboLogs.Instance);

        blockTree.BlocksFinalized += Raise.EventWith(new FinalizeEventArgs(finalized.Header));

        ledger.DidNotReceiveWithAnyArgs().EarnWidthOnFinalization(default!, default!);
        finalizer.Dispose();
    }

    private static (IBlockTree, IReceiptFinder, IFrameTxWidthLedger, ITxPool) Wire(params Block[] canonical)
    {
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        IReceiptFinder receiptFinder = Substitute.For<IReceiptFinder>();
        foreach (Block block in canonical)
        {
            blockTree.FindBlock(block.Number, BlockTreeLookupOptions.RequireCanonical).Returns(block);
            receiptFinder.Get(block, Arg.Any<bool>(), Arg.Any<bool>()).Returns([new TxReceipt { GasUsed = GasUsed }]);
        }

        ITxPool txPool = Substitute.For<ITxPool, IFrameTxWidthLedger>();
        return (blockTree, receiptFinder, (IFrameTxWidthLedger)txPool, txPool);
    }

    private static ITxPoolConfig Enabled() => new TxPoolConfig { FrameTxWidthEnabled = true };

    private static Block FrameBlock(ulong number)
    {
        Transaction tx = Build.A.Transaction
            .WithType(TxType.FrameTx)
            .WithNonce(number)
            .WithSenderAddress(TestItem.AddressA)
            .TestObject;
        return Build.A.Block.WithNumber(number).WithTransactions(tx).TestObject;
    }
}
