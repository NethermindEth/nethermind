// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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

    /// <summary>The block's receipts as the finder returns them: none, or one for each of its transactions.</summary>
    public enum Receipts
    {
        Missing,
        Complete
    }

    /// <remarks>
    /// The block the index names is at height <see cref="Number"/>; a floor of <c>Number + 1</c> puts it below the served
    /// floor, a floor of <see cref="Number"/> at it. The rows that expect <c>false</c> before a body read set every other
    /// condition so that a missing check would answer <c>true</c>.
    /// </remarks>
    [TestCase(false, true, Number + 1, Body.WithTheTransaction, Receipts.Missing, Head, false, TestName = "IsTransactionPruned_no_index_entry")]
    [TestCase(true, false, Number + 1, Body.WithTheTransaction, Receipts.Missing, Head, false, TestName = "IsTransactionPruned_side_chain_block")]
    [TestCase(true, true, Number + 1, Body.NotStored, Receipts.Missing, Head, true, TestName = "IsTransactionPruned_body_not_stored_below_the_floor")]
    [TestCase(true, true, Number, Body.NotStored, Receipts.Missing, Head, false, TestName = "IsTransactionPruned_body_not_stored_at_the_floor")]
    [TestCase(true, true, Number + 1, Body.WithoutTheTransaction, Receipts.Complete, Head, false, TestName = "IsTransactionPruned_body_without_the_transaction_below_the_floor")]
    [TestCase(true, true, Number + 1, Body.WithTheTransaction, Receipts.Complete, Head, true, TestName = "IsTransactionPruned_body_with_the_transaction_below_the_floor")]
    [TestCase(true, true, Number, Body.WithTheTransaction, Receipts.Missing, Head, true, TestName = "IsTransactionPruned_receipts_missing_at_the_floor")]
    [TestCase(true, true, Number, Body.WithTheTransaction, Receipts.Complete, Head, false, TestName = "IsTransactionPruned_receipts_stored_at_the_floor")]
    [TestCase(true, true, Number, Body.WithTheTransaction, Receipts.Missing, Number / 2, false, TestName = "IsTransactionPruned_receipts_missing_above_the_head")]
    [TestCase(true, true, Number, Body.WithoutTheTransaction, Receipts.Missing, Head, false, TestName = "IsTransactionPruned_body_without_the_transaction_receipts_missing_at_the_floor")]
    public void IsTransactionPruned_decides_from_the_index_entry_the_body_and_the_receipts(
        bool indexed, bool canonical, ulong floor, Body body, Receipts receipts, ulong head, bool expected)
    {
        Transaction[] transactions = Enumerable.Range(0, 2)
            .Select(static i => Build.A.Transaction.WithNonce((ulong)i).SignedAndResolved(TestItem.PrivateKeyB).TestObject)
            .ToArray();
        Block block = Build.A.Block.WithNumber(Number).WithTransactions(transactions).TestObject;
        // A compact entry of a transaction reorganised out of this height names the block now canonical there.
        Hash256 txHash = body is Body.WithoutTheTransaction ? TestItem.KeccakD : transactions[1].Hash!;

        IReceiptFinder receiptFinder = Substitute.For<IReceiptFinder>();
        if (indexed) receiptFinder.FindBlockHash(txHash).Returns(block.Hash);
        receiptFinder.Get(block, Arg.Any<bool>(), Arg.Any<bool>()).Returns(receipts is Receipts.Complete
            ? transactions.Select(static (tx, i) => new TxReceipt { TxHash = tx.Hash, Index = i }).ToArray()
            : []);

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
        // A hash without an entry, which is every poll for a pending transaction, does nothing past the index read; an
        // entry that is not canonical reads no body.
        if (!indexed) blockFinder.DidNotReceiveWithAnyArgs().FindHeader(null!, default, default);
        if (!indexed || !canonical)
        {
            blockFinder.DidNotReceiveWithAnyArgs().FindBlock(null!, default, default);
            receiptFinder.DidNotReceiveWithAnyArgs().Get(null!, default, default);
        }
    }
}
