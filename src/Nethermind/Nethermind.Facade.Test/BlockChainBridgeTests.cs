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

using System;
using System.Linq;
using FluentAssertions;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Filters;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.Db.Blooms;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.TxPool;
using Nethermind.Wallet;
using NSubstitute;
using NUnit.Framework;
using System.Threading;

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
        private ManualTimestamper _timestamper;
        private ISpecProvider _specProvider;
        private IDbProvider _dbProvider;

        [SetUp]
        public void SetUp()
        {
            _dbProvider = new MemDbProvider();

            _stateReader = new StateReader(_dbProvider.StateDb, _dbProvider.CodeDb, LimboLogs.Instance);
            _stateProvider = new StateProvider(_dbProvider.StateDb, _dbProvider.CodeDb, LimboLogs.Instance);
            _storageProvider = new StorageProvider(_dbProvider.StateDb, _stateProvider, LimboLogs.Instance);
          
            _timestamper = new ManualTimestamper();
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
                _timestamper,
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
            _timestamper.UtcNow = DateTime.MinValue;
            _timestamper.Add(TimeSpan.FromDays(123));
            BlockHeader header = Build.A.BlockHeader.WithNumber(10).TestObject;
            Transaction tx = new Transaction();
            tx.GasLimit = Transaction.BaseTxGasCost;
            
            var gas = _blockchainBridge.EstimateGas(header, tx, default(CancellationToken));
            gas.GasSpent.Should().Be(Transaction.BaseTxGasCost);

            _transactionProcessor.Received().CallAndRestore(
                tx,
                Arg.Is<BlockHeader>(bh => bh.Number == 11 && bh.Timestamp == ((ITimestamper)_timestamper).EpochSeconds),
                Arg.Any<EstimateGasTracer>());
        }

        [Test]
        public void Get_storage()
        {
            /* all testing will be touching just a single storage cell */
            var storageCell = new StorageCell(TestItem.AddressA, 1);
            
            /* to start with we need to create an account that we will be setting storage at */
            _stateProvider.CreateAccount(storageCell.Address, UInt256.One);
            _stateProvider.Commit(MuirGlacier.Instance);
            _stateProvider.CommitTree();
            
            /* at this stage we have an account with empty storage at the address that we want to test */

            byte[] initialValue = new byte[] {1, 2, 3};
            _storageProvider.Set(storageCell, initialValue);
            _storageProvider.Commit();
            _storageProvider.CommitTrees();
            _stateProvider.Commit(MuirGlacier.Instance);
            _stateProvider.CommitTree();

            var retrieved =
                _blockchainBridge.GetStorage(storageCell.Address, storageCell.Index, _stateProvider.StateRoot);
            retrieved.Should().BeEquivalentTo(initialValue);
            
            /* at this stage we set the value in storage to 1,2,3 at the tested storage cell */
            
            /* Now we are testing scenario where the storage is being changed by the block processor.
               To do that we create some different storage / state access stack that represents the processor.
               It is a different stack of objects than the one that is used by the blockchain bridge. */
            
            byte[] newValue = new byte[] {1, 2, 3, 4, 5};
            
            StateProvider processorStateProvider =
                new StateProvider(_dbProvider.StateDb, _dbProvider.CodeDb, LimboLogs.Instance);
            processorStateProvider.StateRoot = _stateProvider.StateRoot;
            
            StorageProvider processorStorageProvider =
                new StorageProvider(_dbProvider.StateDb, processorStateProvider, LimboLogs.Instance);
            
            processorStorageProvider.Set(storageCell, newValue);
            processorStorageProvider.Commit();
            processorStorageProvider.CommitTrees();
            processorStateProvider.Commit(MuirGlacier.Instance);
            processorStateProvider.CommitTree();
            
            /* At this stage the DB should have the storage value updated to 5.
               We will try to retrieve the value by taking the state root from the processor.*/
            
            retrieved =
                _blockchainBridge.GetStorage(storageCell.Address, storageCell.Index, processorStateProvider.StateRoot);
            retrieved.Should().BeEquivalentTo(newValue);
            
            /* If it failed then it means that the blockchain bridge cached the previous call value */
        }
    }
}