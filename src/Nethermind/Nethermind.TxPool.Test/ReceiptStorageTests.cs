// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Blocks;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Specs;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.TxPool.Test
{
    [TestFixture(true)]
    [TestFixture(false)]
    [Parallelizable(ParallelScope.All)]
    [FixtureLifeCycle(LifeCycle.InstancePerTestCase)]
    public class ReceiptStorageTests
    {
        private readonly bool _useEip2718;
        private ISpecProvider _specProvider;
        private IEthereumEcdsa _ethereumEcdsa;
        private IReceiptStorage _persistentStorage;
        private IReceiptFinder _receiptFinder;
        private IReceiptStorage _inMemoryStorage;
        private IBlockTree _blockTree;
        private IBlockStore _blockStore;

        public ReceiptStorageTests(bool useEip2718)
        {
            _useEip2718 = useEip2718;
        }

        [SetUp]
        public void Setup()
        {
            _specProvider = MainnetSpecProvider.Instance;
            _ethereumEcdsa = new EthereumEcdsa(_specProvider.ChainId);
            _blockTree = Build.A.BlockTree()
                .WithBlocks(Build.A.Block.TestObject)
                .TestObject;
            _blockStore = Substitute.For<IBlockStore>();
            ReceiptsRecovery receiptsRecovery = new(_ethereumEcdsa, _specProvider);
            _persistentStorage = new PersistentReceiptStorage(
                new MemColumnsDb<ReceiptsColumns>(),
                _specProvider,
                receiptsRecovery,
                _blockTree,
                _blockStore,
                new ReceiptConfig()
            );
            _receiptFinder = new FullInfoReceiptFinder(_persistentStorage, receiptsRecovery, Substitute.For<IBlockFinder>());
            _inMemoryStorage = new InMemoryReceiptStorage();
        }

        [Test]
        public void should_add_and_fetch_receipt_from_in_memory_storage()
            => TestAddAndGetReceipt(_inMemoryStorage, clearSender: false);

        [Test]
        public void should_add_and_fetch_receipt_from_persistent_storage()
            => TestAddAndGetReceipt(_persistentStorage, _receiptFinder, clearSender: true);

        [Test]
        public void should_add_and_fetch_receipt_from_persistent_storage_with_eip_658()
            => TestAddAndGetReceipt(_persistentStorage, clearSender: false);

        [TestCase(true, TestName = "should_not_throw_if_receiptFinder_asked_for_not_existing_receipts_by_block")]
        [TestCase(false, TestName = "should_not_throw_if_receiptFinder_asked_for_not_existing_receipts_by_hash")]
        public void should_not_throw_if_receiptFinder_asked_for_not_existing_receipts(bool byBlock)
        {
            Block block = Build.A.Block.WithNumber(0).WithTransactions(5, _specProvider).TestObject;
            TxReceipt[] receipts = byBlock ? _receiptFinder.Get(block) : _receiptFinder.Get(block.Hash);
            receipts.Should().BeEmpty();
        }

        private void TestAddAndGetReceipt(IReceiptStorage storage, IReceiptFinder receiptFinder = null, bool clearSender = false)
        {
            receiptFinder ??= storage;

            Transaction transaction = GetSignedTransaction();
            if (clearSender)
            {
                transaction.SenderAddress = null;
            }
            Block block = GetBlock(transaction);
            _blockTree.Insert(block, BlockTreeInsertBlockOptions.SaveHeader);
            TxReceipt receipt = GetReceipt(transaction, block);
            storage.Insert(block, receipt);
            receipt = storage.Get(block)[0];
            Hash256 blockHash = storage.FindBlockHash(transaction.Hash);
            blockHash.Should().Be(block.Hash);
            TxReceipt fetchedReceipt = receiptFinder.Get(block).ForTransaction(transaction.Hash);
            receipt.StatusCode.Should().Be(fetchedReceipt.StatusCode);
            receipt.PostTransactionState.Should().Be(fetchedReceipt.PostTransactionState);
            receipt.TxHash.Should().Be(transaction.Hash);
            if (clearSender)
            {
                receipt.Sender.Should().BeEquivalentTo(TestItem.AddressA);
            }
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
