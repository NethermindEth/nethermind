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
using Nethermind.Blockchain.Filters;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.Db.Blooms;
using Nethermind.Specs;
using Nethermind.TxPool;
using Nethermind.Wallet;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Facade.Test
{
    public class BlockchainBridgeTests
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
        private ISpecProvider _specProvider;

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
            _specProvider = MainnetSpecProvider.Instance;
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
                LimboLogs.Instance,
                false);
        }

        [Test]
        public void get_transaction_returns_null_when_transaction_not_found()
        {
            _blockchainBridge.GetTransaction(TestItem.KeccakA).Should().Be((null, null));
        }
        
        [Test]
        public void get_transaction_returns_null_when_block_not_found()
        {
            var receipt = Build.A.Receipt.WithBlockHash(TestItem.KeccakB).TestObject;
            _receiptStorage.FindBlockHash(TestItem.KeccakA).Returns(TestItem.KeccakB);
            _blockchainBridge.GetTransaction(TestItem.KeccakA).Should().Be((null, null));
        }
        
        [Test]
        public void get_transaction_returns_receipt_and_transaction_when_found()
        {
            int index = 5;
            var receipt = Build.A.Receipt.WithBlockHash(TestItem.KeccakB).WithTransactionHash(TestItem.KeccakA).WithIndex(index).TestObject;
            var block = Build.A.Block.WithTransactions(Enumerable.Range(0, 10).Select(i => Build.A.Transaction.WithNonce((UInt256) i).TestObject).ToArray()).TestObject;
            _blockTree.FindBlock(TestItem.KeccakB, Arg.Any<BlockTreeLookupOptions>()).Returns(block);
            _receiptStorage.FindBlockHash(TestItem.KeccakA).Returns(TestItem.KeccakB);
            _receiptStorage.Get(block).Returns(new[] {receipt});
            _blockchainBridge.GetTransaction(TestItem.KeccakA).Should()
                .BeEquivalentTo((receipt, Build.A.Transaction.WithNonce((UInt256) index).TestObject));
        }

        [Test]
        public void Estimate_gas_returns_the_estimate_from_the_tracer()
        {
            BlockHeader header = Build.A.BlockHeader.WithNumber(10).TestObject;
            Transaction tx = new Transaction();
            tx.GasLimit = Transaction.BaseTxGasCost;
            
            var gas = _blockchainBridge.EstimateGas(header, tx);
            gas.GasSpent.Should().Be(Transaction.BaseTxGasCost);
            
            _transactionProcessor.Received().CallAndRestore(
                tx,
                Arg.Is<BlockHeader>(bh => bh.Number == 11),
                Arg.Any<EstimateGasTracer>());
        }
    }
}