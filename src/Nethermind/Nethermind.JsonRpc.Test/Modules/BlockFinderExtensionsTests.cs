// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.JsonRpc.Modules;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Modules;

[Parallelizable(ParallelScope.All)]
public class BlockFinderExtensionsTests
{
    private const ulong Number = 100;
    private const ulong Head = 200;

    public enum Body
    {
        NotStored,
        WithTheTransaction,
        WithoutTheTransaction
    }

    /// <summary>The block's receipts as the finder returns them: none, only the other transaction's, or one for each transaction.</summary>
    public enum Receipts
    {
        Missing,
        Partial,
        Complete
    }

    /// <summary>The last read a case makes, in the order the predicate reads: the index, the canonical header, the body, the receipts.</summary>
    public enum LastRead
    {
        Index,
        Header,
        Body,
        Receipts
    }

    /// <remarks>
    /// The block the index names is at height <see cref="Number"/>; a floor of <c>Number + 1</c> puts it below the served
    /// floor, a floor of <see cref="Number"/> at it, and a head of <c>Number / 2</c> leaves it unsettled. The rows that
    /// expect <c>false</c> before a body read set every other condition so that a missing check would answer <c>true</c>.
    /// </remarks>
    private static IEnumerable<TestCaseData> IsTransactionPrunedCases()
    {
        yield return new TestCaseData(false, true, Number + 1, Body.WithTheTransaction, Receipts.Missing, Head, LastRead.Index, false)
            .SetName("IsTransactionPruned_no_index_entry");
        yield return new TestCaseData(true, false, Number + 1, Body.WithTheTransaction, Receipts.Missing, Head, LastRead.Header, false)
            .SetName("IsTransactionPruned_side_chain_block");
        yield return new TestCaseData(true, true, Number + 1, Body.NotStored, Receipts.Missing, Head, LastRead.Body, true)
            .SetName("IsTransactionPruned_body_not_stored_below_the_floor");
        yield return new TestCaseData(true, true, Number, Body.NotStored, Receipts.Missing, Head, LastRead.Body, false)
            .SetName("IsTransactionPruned_body_not_stored_at_the_floor");
        yield return new TestCaseData(true, true, Number + 1, Body.WithoutTheTransaction, Receipts.Complete, Head, LastRead.Body, false)
            .SetName("IsTransactionPruned_body_without_the_transaction_below_the_floor");
        yield return new TestCaseData(true, true, Number + 1, Body.WithTheTransaction, Receipts.Complete, Head, LastRead.Body, true)
            .SetName("IsTransactionPruned_body_with_the_transaction_below_the_floor");
        yield return new TestCaseData(true, true, Number + 1, Body.WithTheTransaction, Receipts.Missing, Number / 2, LastRead.Body, true)
            .SetName("IsTransactionPruned_body_with_the_transaction_below_the_floor_above_the_head");
        yield return new TestCaseData(true, true, Number, Body.WithTheTransaction, Receipts.Missing, Head, LastRead.Receipts, true)
            .SetName("IsTransactionPruned_receipts_missing_at_the_floor");
        yield return new TestCaseData(true, true, Number, Body.WithTheTransaction, Receipts.Partial, Head, LastRead.Receipts, true)
            .SetName("IsTransactionPruned_receipts_partial_at_the_floor");
        yield return new TestCaseData(true, true, Number, Body.WithTheTransaction, Receipts.Complete, Head, LastRead.Receipts, false)
            .SetName("IsTransactionPruned_receipts_stored_at_the_floor");
        yield return new TestCaseData(true, true, Number, Body.WithTheTransaction, Receipts.Missing, Number / 2, LastRead.Header, false)
            .SetName("IsTransactionPruned_receipts_missing_above_the_head");
        yield return new TestCaseData(true, true, Number, Body.WithoutTheTransaction, Receipts.Missing, Head, LastRead.Body, false)
            .SetName("IsTransactionPruned_body_without_the_transaction_receipts_missing_at_the_floor");
    }

    [TestCaseSource(nameof(IsTransactionPrunedCases))]
    public void IsTransactionPruned_decides_from_the_index_entry_the_body_and_the_receipts(
        bool indexed, bool canonical, ulong floor, Body body, Receipts receipts, ulong head, LastRead lastRead, bool expected)
    {
        Transaction[] transactions = Enumerable.Range(0, 2)
            .Select(static i => Build.A.Transaction.WithNonce((ulong)i).SignedAndResolved(TestItem.PrivateKeyB).TestObject)
            .ToArray();
        Block block = Build.A.Block.WithNumber(Number).WithTransactions(transactions).TestObject;
        // A compact entry of a transaction reorganised out of this height names the block now canonical there.
        Hash256 txHash = body is Body.WithoutTheTransaction ? TestItem.KeccakD : transactions[0].Hash!;

        IReceiptFinder receiptFinder = Substitute.For<IReceiptFinder>();
        if (indexed) receiptFinder.FindBlockHash(txHash).Returns(block.Hash);
        receiptFinder.Get(block, Arg.Any<bool>(), Arg.Any<bool>()).Returns(receipts switch
        {
            Receipts.Complete => transactions.Select(static (tx, i) => new TxReceipt { TxHash = tx.Hash, Index = i }).ToArray(),
            Receipts.Partial => [new TxReceipt { TxHash = transactions[1].Hash, Index = 1 }],
            _ => []
        });

        IBlockFinder blockFinder = Substitute.For<IBlockFinder>();
        blockFinder.Head.Returns(Build.A.Block.WithNumber(head).TestObject);
        blockFinder.LowestServedBlock.Returns(floor);
        // A side-chain header is found without the canonical requirement, as the block tree finds it.
        blockFinder.FindHeader(block.Hash!, BlockTreeLookupOptions.None).Returns(block.Header);
        if (canonical) blockFinder.FindHeader(block.Hash!, BlockTreeLookupOptions.RequireCanonical).Returns(block.Header);
        blockFinder.IsMainChain(block.Header).Returns(canonical);
        blockFinder.GetInfo(Number, block.Hash!).Returns(((BlockInfo?)null, (ChainLevelInfo?)null));
        if (body is not Body.NotStored)
            blockFinder.FindBlock(block.Hash!, Arg.Any<BlockTreeLookupOptions>(), Arg.Any<ulong?>()).Returns(block);

        bool pruned = blockFinder.IsTransactionPruned(receiptFinder, txHash);

        Assert.That(pruned, Is.EqualTo(expected));
        if (lastRead < LastRead.Header) blockFinder.DidNotReceiveWithAnyArgs().FindHeader(null!, default, default);
        if (lastRead < LastRead.Body) blockFinder.DidNotReceiveWithAnyArgs().FindBlock(null!, default, default);
        if (lastRead < LastRead.Receipts)
        {
            receiptFinder.DidNotReceiveWithAnyArgs().Get(null!, default, default);
        }
        else
        {
            // Only their number is used: one read, asking for no recovery.
            receiptFinder.ReceivedWithAnyArgs(1).Get(null!, default, default);
            receiptFinder.Received(1).Get(block, recover: false, recoverSender: false);
        }
    }
}
