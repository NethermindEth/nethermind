// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Blocks;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.State;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Modules.Eth;

public partial class EthRpcModuleTests
{
    public enum StoredReceipts { Actual, ForeignSender, MissingSender, FewerThanTransactions }

    [Test]
    public async Task Eth_get_transaction_by_block_number_and_index_takes_the_stored_sender_of_an_uncached_block([Values] StoredReceipts storedReceipts)
    {
        Address?[]? storedSenders = null;
        using Context ctx = await Context.Create(configurer: builder =>
            builder.AddDecorator<IReceiptFinder>((_, inner) => new IteratorOverrideReceiptFinder(inner,
                () => storedSenders?.Select(static sender => Build.A.Receipt.WithSender(sender!).TestObject).ToArray())));
        TestRpcBlockchain test = ctx.Test;
        ulong blockNumber = await AddBlockWithTwoTransactionsFromB(test);

        storedSenders = storedReceipts switch
        {
            StoredReceipts.ForeignSender => [TestItem.AddressE, TestItem.AddressD],
            StoredReceipts.MissingSender => [TestItem.AddressE, null],
            StoredReceipts.FewerThanTransactions => [TestItem.AddressD],
            _ => null
        };
        ((IClearableCache)test.Container.Resolve<IBlockStore>()).ClearCache();

        ResultWrapper<TransactionForRpc> result = test.EthRpcModule.eth_getTransactionByBlockNumberAndIndex(new BlockParameter(blockNumber), 1);

        Assert.That(((LegacyTransactionForRpc)result.Data).From,
            Is.EqualTo(storedReceipts == StoredReceipts.ForeignSender ? TestItem.AddressD : TestItem.AddressB));
    }

    [Test]
    public async Task Eth_get_transaction_by_block_number_and_index_does_not_regenerate_receipts_for_the_sender()
    {
        bool regenerationForbidden = false;
        using Context ctx = await Context.Create(configurer: builder =>
            builder.AddKeyedSingleton<IReceiptFinder>(IReceiptFinder.RegenerableKey, c =>
                new IteratorOverrideReceiptFinder(c.Resolve<IReceiptFinder>(), () => regenerationForbidden
                    ? throw new InvalidOperationException("The sender lookup must not read regenerable receipts.")
                    : null)));
        TestRpcBlockchain test = ctx.Test;
        ulong blockNumber = await AddBlockWithTwoTransactionsFromB(test);
        regenerationForbidden = true;
        ((IClearableCache)test.Container.Resolve<IBlockStore>()).ClearCache();

        ResultWrapper<TransactionForRpc> result = test.EthRpcModule.eth_getTransactionByBlockNumberAndIndex(new BlockParameter(blockNumber), 1);

        Assert.That(((LegacyTransactionForRpc)result.Data).From, Is.EqualTo(TestItem.AddressB));
    }

    /// <summary>Adds a block with two transactions sent by <see cref="TestItem.AddressB"/>, then one more on top of it.</summary>
    private static async Task<ulong> AddBlockWithTwoTransactionsFromB(TestRpcBlockchain test)
    {
        ulong nonce = test.StateReader.GetNonce(test.BlockTree.Head!.Header, TestItem.AddressB);
        Transaction[] transactions = Enumerable.Range(0, 2)
            .Select(i => Build.A.Transaction.WithNonce(nonce + (ulong)i).WithTo(TestItem.AddressC).SignedAndResolved(TestItem.PrivateKeyB).TestObject)
            .ToArray();
        await test.AddBlock(transactions);
        ulong blockNumber = test.BlockTree.Head!.Number;
        await test.AddBlock();
        test.Container.Resolve<IDeferredBlockDataWriter>().Drain();
        return blockNumber;
    }

    /// <summary>Serves the receipts from <paramref name="iteratorReceipts"/> through the iterator, or the inner finder's when it returns null.</summary>
    private sealed class IteratorOverrideReceiptFinder(IReceiptFinder inner, Func<TxReceipt[]?> iteratorReceipts) : IReceiptFinder
    {
        public Hash256? FindBlockHash(Hash256 txHash) => inner.FindBlockHash(txHash);
        public TxReceipt[] Get(Block block, bool recover = true, bool recoverSender = true) => inner.Get(block, recover, recoverSender);
        public TxReceipt[] Get(Hash256 blockHash, bool recover = true) => inner.Get(blockHash, recover);
        public bool CanGetReceiptsByHash(ulong blockNumber) => inner.CanGetReceiptsByHash(blockNumber);

        public bool TryGetReceiptsIterator(ulong blockNumber, Hash256 blockHash, out ReceiptsIterator iterator)
        {
            if (iteratorReceipts() is not { } receipts) return inner.TryGetReceiptsIterator(blockNumber, blockHash, out iterator);

            iterator = new ReceiptsIterator(receipts);
            return true;
        }
    }
}
