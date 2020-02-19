//  Copyright (c) 2018 Demerzel Solutions Limited
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

using System.Linq;
using FluentAssertions;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Bloom;
using Nethermind.Blockchain.Filters;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.TxPools;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Evm;
using Nethermind.Store;
using Nethermind.Wallet;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Facade.Test
{
    public class BlockChainBridgeTests
    {
        private BlockchainBridge _blockchainBridge;
        private IStateReader _stateReader;
        private IStateProvider _stateProvider;
        private IStorageProvider _storageProvider;
        private IBlockTree _blockTree;
        private ITxPool _txPool;
        private IReceiptStorage _receiptStorage;
        private IFilterStore _filterStore;
        private IFilterManager _filterManager;
        private IWallet _wallet;
        private ITransactionProcessor _transactionProcessor;
        private IEthereumEcdsa _ethereumEcdsa;
        private IBloomStorage _bloomStorage;
        private IReceiptsRecovery _receiptsRecovery;

        [SetUp]
        public void SetUp()
        {
            _stateReader = Substitute.For<IStateReader>();
            _stateProvider = Substitute.For<IStateProvider>();
            _storageProvider = Substitute.For<IStorageProvider>();
            _blockTree = Substitute.For<IBlockTree>();
            _txPool = Substitute.For<ITxPool>();
            _receiptStorage = Substitute.For<IReceiptStorage>();
            _filterStore = Substitute.For<IFilterStore>();
            _filterManager = Substitute.For<IFilterManager>();
            _wallet = Substitute.For<IWallet>();
            _transactionProcessor = Substitute.For<ITransactionProcessor>();
            _ethereumEcdsa = Substitute.For<IEthereumEcdsa>();
            _bloomStorage = Substitute.For<IBloomStorage>();
            _receiptsRecovery = Substitute.For<IReceiptsRecovery>();
            _blockchainBridge = new BlockchainBridge(
                _stateReader, 
                _stateProvider,
                _storageProvider,
                _blockTree,
                _txPool,
                _receiptStorage,
                _filterStore,
                _filterManager,
                _wallet,
                _transactionProcessor,
                _ethereumEcdsa,
                _bloomStorage,
                _receiptsRecovery);
        }

        [Test]
        public void get_transaction_returns_null_when_transaction_not_found()
        {
            _blockchainBridge.GetTransaction(TestItem.KeccakA).Should().Be((null, null));
        }
        
        [Test]
        public void get_transaction_returns_receipt_when_found()
        {
            var receipt = Build.A.Receipt.WithBlockHash(TestItem.KeccakB).TestObject;
            _receiptStorage.Find(TestItem.KeccakA).Returns(receipt);
            _blockchainBridge.GetTransaction(TestItem.KeccakA).Should().BeEquivalentTo((receipt, (Transaction) null));
        }
        
        [Test]
        public void get_transaction_returns_receipt_and_transaction_when_found()
        {
            int index = 5;
            var receipt = Build.A.Receipt.WithBlockHash(TestItem.KeccakB).WithIndex(index).TestObject;
            _blockTree.FindBlock(TestItem.KeccakB, BlockTreeLookupOptions.RequireCanonical).Returns(
                Build.A.Block.WithTransactions(
                    Enumerable.Range(0, 10).Select(i => Build.A.Transaction.WithNonce((UInt256) i).TestObject).ToArray()
                ).TestObject);
            _receiptStorage.Find(TestItem.KeccakA).Returns(receipt);
            _blockchainBridge.GetTransaction(TestItem.KeccakA).Should()
                .BeEquivalentTo((receipt, (Transaction) Build.A.Transaction.WithNonce((UInt256) index).TestObject));
        }
        
        [Test]
        public void get_transaction_returns_pending_transaction_when_found()
        {
            UInt256 nonce = 5;
            _txPool.TryGetPendingTransaction(TestItem.KeccakA, out Arg.Any<Transaction>()).Returns(x =>
            {
                x[1] = Build.A.Transaction.WithNonce(nonce).TestObject;
                return true;
            });
            _blockchainBridge.GetTransaction(TestItem.KeccakA).Should().BeEquivalentTo(((TxReceipt) null, Build.A.Transaction.WithNonce(nonce).TestObject));
        }
        
        [Test]
        public void get_pending_transactions_returns_tx_pool_pending_transactions()
        {
            var transactions = Enumerable.Range(0, 10).Select(i => Build.A.Transaction.WithNonce((UInt256) i).TestObject).ToArray();
            _txPool.GetPendingTransactions().Returns(transactions);
            _blockchainBridge.GetPendingTransactions().Should().BeEquivalentTo(transactions);
        }
    }
}