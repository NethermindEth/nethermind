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

using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Filters;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.TxPool;
using NSubstitute;
using NUnit.Framework;
using System.Threading;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Processing;
using Nethermind.Trie.Pruning;
using System.Threading.Tasks;
using Nethermind.Evm.TransactionProcessing;

namespace Nethermind.Facade.Test
{
    public class BlockchainBridgeTests
    {
        private BlockchainBridge _blockchainBridge;
        private IBlockTree _blockTree;
        private ITxPool _txPool;
        private IReceiptStorage _receiptStorage;
        private IFilterStore _filterStore;
        private IFilterManager _filterManager;
        private ITransactionProcessor _transactionProcessor;
        private IEthereumEcdsa _ethereumEcdsa;
        private ManualTimestamper _timestamper;
        private ISpecProvider _specProvider;
        private IDbProvider _dbProvider;

        [SetUp]
        public async Task SetUp()
        {
            _dbProvider = await TestMemDbProvider.InitAsync();
            _timestamper = new ManualTimestamper();
            _blockTree = Substitute.For<IBlockTree>();
            _txPool = Substitute.For<ITxPool>();
            _receiptStorage = Substitute.For<IReceiptStorage>();
            _filterStore = Substitute.For<IFilterStore>();
            _filterManager = Substitute.For<IFilterManager>();
            _transactionProcessor = Substitute.For<ITransactionProcessor>();
            _ethereumEcdsa = Substitute.For<IEthereumEcdsa>();
            _specProvider = MainnetSpecProvider.Instance;

            ReadOnlyTxProcessingEnv processingEnv = new ReadOnlyTxProcessingEnv(
                new ReadOnlyDbProvider(_dbProvider, false),
                new TrieStore(_dbProvider.StateDb, LimboLogs.Instance).AsReadOnly(), 
                new ReadOnlyBlockTree(_blockTree),
                _specProvider,
                LimboLogs.Instance);

            processingEnv.TransactionProcessor = _transactionProcessor;

            _blockchainBridge = new BlockchainBridge(
                processingEnv,
                _txPool,
                _receiptStorage,
                _filterStore,
                _filterManager,
                _ethereumEcdsa,
                _timestamper,
                Substitute.For<ILogFinder>(),
                _specProvider,
                false,
                false);
        }

        [Test]
        public void get_transaction_returns_null_when_transaction_not_found()
        {
            _blockchainBridge.GetTransaction(TestItem.KeccakA).Should().Be((null, null, null));
        }

        [Test]
        public void get_transaction_returns_null_when_block_not_found()
        {
            _receiptStorage.FindBlockHash(TestItem.KeccakA).Returns(TestItem.KeccakB);
            _blockchainBridge.GetTransaction(TestItem.KeccakA).Should().Be((null, null, null));
        }

        [Test]
        public void get_transaction_returns_receipt_and_transaction_when_found()
        {
            int index = 5;
            var receipt = Build.A.Receipt
                .WithBlockHash(TestItem.KeccakB)
                .WithTransactionHash(TestItem.KeccakA)
                .WithIndex(index)
                .TestObject;
            IEnumerable<Transaction> transactions = Enumerable.Range(0, 10)
                .Select(i => Build.A.Transaction.WithNonce((UInt256) i).TestObject);
            var block = Build.A.Block
                .WithTransactions(transactions.ToArray())
                .TestObject;
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
            tx.Data = new byte[0];
            tx.GasLimit = Transaction.BaseTxGasCost;

            var gas = _blockchainBridge.EstimateGas(header, tx, default);
            gas.GasSpent.Should().Be(Transaction.BaseTxGasCost);

            _transactionProcessor.Received().CallAndRestore(
                tx,
                Arg.Is<BlockHeader>(bh => bh.Number == 11 && bh.Timestamp == ((ITimestamper) _timestamper).UnixTime.Seconds),
                Arg.Is<CancellationTxTracer>(t => t.InnerTracer is EstimateGasTracer));
        }

        [Test]
        public void Call_uses_valid_block_number()
        {
            _timestamper.UtcNow = DateTime.MinValue;
            _timestamper.Add(TimeSpan.FromDays(123));
            BlockHeader header = Build.A.BlockHeader.WithNumber(10).TestObject;
            Transaction tx = new Transaction();
            tx.GasLimit = Transaction.BaseTxGasCost;

            _blockchainBridge.Call(header, tx, CancellationToken.None);
            _transactionProcessor.Received().CallAndRestore(
                tx,
                Arg.Is<BlockHeader>(bh => bh.Number == 10),
                Arg.Any<ITxTracer>());
        }

        [TestCase(true, 0, 8)]
        [TestCase(true, 7, 7)]
        [TestCase(false, 0, 0)]
        [TestCase(false, 7, 7)]
        public void Bridge_beam_head_is_correct(bool isBeam, long headNumber, long? expectedNumber)
        {
            ReadOnlyTxProcessingEnv processingEnv = new ReadOnlyTxProcessingEnv(
                new ReadOnlyDbProvider(_dbProvider, false),
                new TrieStore(_dbProvider.StateDb, LimboLogs.Instance).AsReadOnly(), 
                new ReadOnlyBlockTree(_blockTree),
                _specProvider,
                LimboLogs.Instance);

            Block head = Build.A.Block.WithNumber(headNumber).TestObject;
            Block bestSuggested = Build.A.Block.WithNumber(8).TestObject;

            _blockTree.Head.Returns(head);
            _blockTree.BestSuggestedBody.Returns(bestSuggested);

            _blockchainBridge = new BlockchainBridge(
                processingEnv,
                _txPool,
                _receiptStorage,
                _filterStore,
                _filterManager,
                _ethereumEcdsa,
                _timestamper,
                Substitute.For<ILogFinder>(),
                _specProvider,
                false,
                isBeam);

            if (expectedNumber.HasValue)
            {
                _blockchainBridge.BeamHead.Number.Should().Be(expectedNumber);
            }
            else
            {
                _blockchainBridge.BeamHead.Should().BeNull();
            }
        }

        [Test]
        public void Bridge_beam_head_is_correct_in_beam()
        {
            ReadOnlyTxProcessingEnv processingEnv = new ReadOnlyTxProcessingEnv(
                new ReadOnlyDbProvider(_dbProvider, false),
                new TrieStore(_dbProvider.StateDb, LimboLogs.Instance).AsReadOnly(), 
                new ReadOnlyBlockTree(_blockTree),
                _specProvider,
                LimboLogs.Instance);

            Block block = Build.A.Block.WithNumber(7).TestObject;
            _blockTree.Head.Returns(block);

            _blockchainBridge = new BlockchainBridge(
                processingEnv,
                _txPool,
                _receiptStorage,
                _filterStore,
                _filterManager,
                _ethereumEcdsa,
                _timestamper,
                Substitute.For<ILogFinder>(),
                _specProvider,
                false,
                false);

            _blockchainBridge.BeamHead.Number.Should().Be(7);
        }
    }
}
