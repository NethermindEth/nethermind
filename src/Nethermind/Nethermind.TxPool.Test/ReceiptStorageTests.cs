// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Specs;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.TxPool.Test
{
    [TestFixture(true)]
    [TestFixture(false)]
    public class ReceiptStorageTests
    {
        private readonly bool _useEip2718;
        private ISpecProvider _specProvider;
        private IEthereumEcdsa _ethereumEcdsa;
        private IReceiptStorage _persistentStorage;
        private IReceiptFinder _receiptFinder;
        private IReceiptStorage _inMemoryStorage;
        private IBlockTree _blockTree;

        public ReceiptStorageTests(bool useEip2718)
        {
            _useEip2718 = useEip2718;
        }

        [SetUp]
        public void Setup()
        {
            _specProvider = RopstenSpecProvider.Instance;
            _ethereumEcdsa = new EthereumEcdsa(_specProvider.ChainId, LimboLogs.Instance);
            _blockTree = Build.A.BlockTree()
                .WithBlocks(Build.A.Block.TestObject)
                .TestObject;
            ReceiptsRecovery receiptsRecovery = new(_ethereumEcdsa, _specProvider);
            _persistentStorage = new PersistentReceiptStorage(
                new MemColumnsDb<ReceiptsColumns>(),
                _specProvider,
                receiptsRecovery,
                _blockTree,
                new ReceiptConfig()
            );
            _receiptFinder = new FullInfoReceiptFinder(_persistentStorage, receiptsRecovery, Substitute.For<IBlockFinder>());
            _inMemoryStorage = new InMemoryReceiptStorage();
        }

        [Test]
        public void should_update_lowest_when_needed_in_memory()
            => TestAddAndCheckLowest(_inMemoryStorage, true);

        [Test]
        public void should_update_lowest_when_needed_persistent()
            => TestAddAndCheckLowest(_persistentStorage, true);

        [Test]
        public void should_not_update_lowest_when_not_needed_persistent()
            => TestAddAndCheckLowest(_persistentStorage, false);

        [Test]
        public void should_not_update_lowest_when_not_needed_in_memory()
            => TestAddAndCheckLowest(_inMemoryStorage, false);

        [Test]
        public void should_add_and_fetch_receipt_from_in_memory_storage()
            => TestAddAndGetReceipt(_inMemoryStorage);

        [Test]
        public void should_add_and_fetch_receipt_from_persistent_storage()
            => TestAddAndGetReceipt(_persistentStorage, _receiptFinder);

        [Test]
        public void should_add_and_fetch_receipt_from_persistent_storage_with_eip_658()
            => TestAddAndGetReceiptEip658(_persistentStorage);

        [Test]
        public void should_not_throw_if_receiptFinder_asked_for_not_existing_receipts_by_block()
        {
            Block block = Build.A.Block.WithNumber(0).WithTransactions(5, _specProvider).TestObject;
            TxReceipt[] receipts = _receiptFinder.Get(block);
            receipts.Should().BeEmpty();
        }

        [Test]
        public void should_not_throw_if_receiptFinder_asked_for_not_existing_receipts_by_hash()
        {
            Block block = Build.A.Block.WithNumber(0).WithTransactions(5, _specProvider).TestObject;
            TxReceipt[] receipts = _receiptFinder.Get(block.Hash);
            receipts.Should().BeEmpty();
        }

        private void TestAddAndCheckLowest(IReceiptStorage storage, bool updateLowest)
        {
            Transaction transaction = GetSignedTransaction();
            Block block = GetBlock(transaction);
            TxReceipt receipt = GetReceipt(transaction, block);
            storage.Insert(block, receipt);
            if (updateLowest)
            {
                storage.LowestInsertedReceiptBlockNumber = block.Number;
            }

            storage.LowestInsertedReceiptBlockNumber.Should().Be(updateLowest ? 1 : null);
        }

        private void TestAddAndGetReceipt(IReceiptStorage storage, IReceiptFinder receiptFinder = null)
        {
            bool recoverSender = receiptFinder is not null;
            receiptFinder ??= storage;

            var transaction = GetSignedTransaction();
            transaction.SenderAddress = null;
            var block = GetBlock(transaction);
            _blockTree.Insert(block, BlockTreeInsertBlockOptions.SaveHeader);
            var receipt = GetReceipt(transaction, block);
            storage.Insert(block, receipt);
            receipt = storage.Get(block)[0];
            var blockHash = storage.FindBlockHash(transaction.Hash);
            blockHash.Should().Be(block.Hash);
            var fetchedReceipt = receiptFinder.Get(block).ForTransaction(transaction.Hash);
            receipt.StatusCode.Should().Be(fetchedReceipt.StatusCode);
            receipt.PostTransactionState.Should().Be(fetchedReceipt.PostTransactionState);
            receipt.TxHash.Should().Be(transaction.Hash);
            if (recoverSender)
            {
                receipt.Sender.Should().BeEquivalentTo(TestItem.AddressA);
            }
        }

        private void TestAddAndGetReceiptEip658(IReceiptStorage storage)
        {
            var transaction = GetSignedTransaction();
            var block = GetBlock(transaction);
            _blockTree.Insert(block, BlockTreeInsertBlockOptions.SaveHeader);
            var receipt = GetReceipt(transaction, block);
            storage.Insert(block, receipt);
            receipt = storage.Get(block)[0];
            var blockHash = storage.FindBlockHash(transaction.Hash);
            blockHash.Should().Be(block.Hash);
            var fetchedReceipt = storage.Get(block).ForTransaction(transaction.Hash);
            receipt.StatusCode.Should().Be(fetchedReceipt.StatusCode);
            receipt.PostTransactionState.Should().Be(fetchedReceipt.PostTransactionState);
            receipt.TxHash.Should().Be(transaction.Hash);
        }

        private Transaction GetSignedTransaction(Address to = null)
            => Build.A.Transaction.SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).TestObject;

        private static TxReceipt GetReceipt(Transaction transaction, Block block)
            => Build.A.Receipt.WithState(TestItem.KeccakB)
                .WithTransactionHash(transaction.Hash)
                .WithBlockHash(block.Hash).TestObject;

        private Block GetBlock(Transaction transaction) =>
            Build.A.Block.WithNumber(1)
                .WithParent(_blockTree.Genesis)
                .WithTransactions(transaction)
                .WithReceiptsRoot(TestItem.KeccakA).TestObject;
    }
}
