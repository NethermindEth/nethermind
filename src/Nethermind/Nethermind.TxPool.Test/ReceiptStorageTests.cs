//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using FluentAssertions;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Specs;
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
        private IReceiptStorage _inMemoryStorage;
        
        public ReceiptStorageTests(bool useEip2718)
        {
            _useEip2718 = useEip2718;
        }
        
        [SetUp]
        public void Setup()
        {
            _specProvider = RopstenSpecProvider.Instance;
            _ethereumEcdsa = new EthereumEcdsa(_specProvider.ChainId, LimboLogs.Instance);
            ReceiptsRecovery receiptsRecovery = new ReceiptsRecovery(_ethereumEcdsa, _specProvider);
            _persistentStorage = new PersistentReceiptStorage(new MemColumnsDb<ReceiptsColumns>(), _specProvider, receiptsRecovery);
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
            => TestAddAndGetReceipt(_inMemoryStorage, false);

        [Test]
        public void should_add_and_fetch_receipt_from_persistent_storage()
            => TestAddAndGetReceipt(_persistentStorage, true);
        
        [Test]
        public void should_add_and_fetch_receipt_from_persistent_storage_with_eip_658()
            => TestAddAndGetReceiptEip658(_persistentStorage);

        private void TestAddAndCheckLowest(IReceiptStorage storage, bool updateLowest)
        {
            var transaction = GetSignedTransaction();
            var block = GetBlock(transaction);
            var receipt = GetReceipt(transaction, block);
            storage.Insert(block, receipt);
            if (updateLowest)
            {
                storage.LowestInsertedReceiptBlockNumber = block.Number;
            }

            storage.LowestInsertedReceiptBlockNumber.Should().Be(updateLowest ? (long?)0 : null);
        }
        
        private void TestAddAndGetReceipt(IReceiptStorage storage, bool recoverSender)
        {
            var transaction = GetSignedTransaction();
            transaction.SenderAddress = null;
            var block = GetBlock(transaction);
            var receipt = GetReceipt(transaction, block);
            storage.Insert(block, receipt);
            var blockHash = storage.FindBlockHash(transaction.Hash);
            blockHash.Should().Be(block.Hash);
            var fetchedReceipt = storage.Get(block).ForTransaction(transaction.Hash);
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
            var receipt = GetReceipt(transaction, block);
            storage.Insert(block, receipt);
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
            Build.A.Block.WithNumber(0)
                .WithTransactions(transaction)
                .WithReceiptsRoot(TestItem.KeccakA).TestObject;
    }
}
