// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Specs;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test.Receipts;

[Parallelizable(ParallelScope.All)]
public class FullInfoReceiptFinderTests
{
    [Test]
    public void Get_by_hash_does_not_load_block_when_only_sender_is_missing()
    {
        Block block = BuildBlock();
        TxReceipt[] receipts = [Build.A.Receipt.WithBlockHash(block.Hash).WithTransactionHash(block.Transactions[0].Hash).TestObject];
        FullInfoReceiptFinder finder = CreateFinder(block, receipts, out IReceiptStorage receiptStorage, out IBlockFinder blockFinder);

        TxReceipt[] result = finder.Get(block.Hash!);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.SameAs(receipts));
            Assert.That(result[0].Sender, Is.Null);
        }

        blockFinder.DidNotReceiveWithAnyArgs().FindBlock(default!, default, default);
        receiptStorage.DidNotReceiveWithAnyArgs().Insert(default!, default);
    }

    [Test]
    public void Get_by_hash_recovers_missing_block_hash_from_block()
    {
        Block block = BuildBlock();
        TxReceipt[] receipts = [Build.A.Receipt.WithTransactionHash(block.Transactions[0].Hash).TestObject];
        FullInfoReceiptFinder finder = CreateFinder(block, receipts, out IReceiptStorage receiptStorage, out _);

        TxReceipt[] result = finder.Get(block.Hash!);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result[0].BlockHash, Is.EqualTo(block.Hash));
            Assert.That(result[0].Sender, Is.Null);
        }

        receiptStorage.Received(1).Insert(block, receipts);
    }

    private static Block BuildBlock() => Build.A.Block.WithTransactions(Build.A.Transaction.Signed().TestObject).TestObject;

    private static FullInfoReceiptFinder CreateFinder(Block block, TxReceipt[] receipts, out IReceiptStorage receiptStorage, out IBlockFinder blockFinder)
    {
        receiptStorage = Substitute.For<IReceiptStorage>();
        receiptStorage.Get(block.Hash!).Returns(receipts);
        blockFinder = Substitute.For<IBlockFinder>();
        blockFinder.FindBlock(block.Hash!, BlockTreeLookupOptions.TotalDifficultyNotNeeded).Returns(block);
        MainnetSpecProvider specProvider = MainnetSpecProvider.Instance;
        ReceiptsRecovery receiptsRecovery = new(new EthereumEcdsa(specProvider.ChainId), specProvider);
        return new FullInfoReceiptFinder(receiptStorage, receiptsRecovery, blockFinder);
    }
}
