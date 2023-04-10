// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Filters;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Receipts;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.Trie.Pruning;
using Nethermind.TxPool;
using NSubstitute;
using NUnit.Framework;
using Nethermind.Config;

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

            ReadOnlyTxProcessingEnv processingEnv = new(
                new ReadOnlyDbProvider(_dbProvider, false),
                new TrieStoreByPath(_dbProvider.StateDb, LimboLogs.Instance).AsReadOnly(),
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
                new BlocksConfig(),
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
                .Select(i => Build.A.Transaction.WithNonce((UInt256)i).TestObject);
            var block = Build.A.Block
                .WithTransactions(transactions.ToArray())
                .TestObject;
            _blockTree.FindBlock(TestItem.KeccakB, Arg.Any<BlockTreeLookupOptions>()).Returns(block);
            _receiptStorage.FindBlockHash(TestItem.KeccakA).Returns(TestItem.KeccakB);
            _receiptStorage.Get(block).Returns(new[] { receipt });
            _blockchainBridge.GetTransaction(TestItem.KeccakA).Should()
                .BeEquivalentTo((receipt, Build.A.Transaction.WithNonce((UInt256)index).TestObject));
        }

        [Test]
        public void Estimate_gas_returns_the_estimate_from_the_tracer()
        {
            _timestamper.UtcNow = DateTime.MinValue;
            _timestamper.Add(TimeSpan.FromDays(123));
            BlockHeader header = Build.A.BlockHeader.WithNumber(10).TestObject;
            Transaction tx = new();
            tx.Data = new byte[0];
            tx.GasLimit = Transaction.BaseTxGasCost;

            var gas = _blockchainBridge.EstimateGas(header, tx, default);
            gas.GasSpent.Should().Be(Transaction.BaseTxGasCost);

            _transactionProcessor.Received().CallAndRestore(
                tx,
                Arg.Is<BlockHeader>(bh =>
                    bh.Number == 11 && bh.Timestamp == ((ITimestamper)_timestamper).UnixTime.Seconds),
                Arg.Is<CancellationTxTracer>(t => t.InnerTracer is EstimateGasTracer));
        }

        [Test]
        public void Call_uses_valid_post_merge_and_random_value()
        {
            BlockHeader header = Build.A.BlockHeader
                .WithDifficulty(0)
                .WithMixHash(TestItem.KeccakA)
                .TestObject;

            Transaction tx = Build.A.Transaction.TestObject;

            _blockchainBridge.Call(header, tx, CancellationToken.None);
            _transactionProcessor.Received().CallAndRestore(
                tx,
                Arg.Is<BlockHeader>(header => header.IsPostMerge && header.Random == TestItem.KeccakA),
                Arg.Any<ITxTracer>());
        }

        [Test]
        public void Call_uses_valid_block_number()
        {
            _timestamper.UtcNow = DateTime.MinValue;
            _timestamper.Add(TimeSpan.FromDays(123));
            BlockHeader header = Build.A.BlockHeader.WithNumber(10).TestObject;
            Transaction tx = new() { GasLimit = Transaction.BaseTxGasCost };

            _blockchainBridge.Call(header, tx, CancellationToken.None);
            _transactionProcessor.Received().CallAndRestore(
                tx,
                Arg.Is<BlockHeader>(bh => bh.Number == 10),
                Arg.Any<ITxTracer>());
        }

        [Test]
        public void Call_uses_valid_mix_hash()
        {
            _timestamper.UtcNow = DateTime.MinValue;
            _timestamper.Add(TimeSpan.FromDays(123));
            BlockHeader header = Build.A.BlockHeader.WithMixHash(TestItem.KeccakA).TestObject;
            Transaction tx = new() { GasLimit = Transaction.BaseTxGasCost };

            _blockchainBridge.Call(header, tx, CancellationToken.None);
            _transactionProcessor.Received().CallAndRestore(
                tx,
                Arg.Is<BlockHeader>(bh => bh.MixHash == TestItem.KeccakA),
                Arg.Any<ITxTracer>());
        }

        [Test]
        public void Call_uses_valid_beneficiary()
        {
            _timestamper.UtcNow = DateTime.MinValue;
            _timestamper.Add(TimeSpan.FromDays(123));
            BlockHeader header = Build.A.BlockHeader.WithBeneficiary(TestItem.AddressB).TestObject;
            Transaction tx = new() { GasLimit = Transaction.BaseTxGasCost };

            _blockchainBridge.Call(header, tx, CancellationToken.None);
            _transactionProcessor.Received().CallAndRestore(
                tx,
                Arg.Is<BlockHeader>(bh => bh.Beneficiary == TestItem.AddressB),
                Arg.Any<ITxTracer>());
        }

        [TestCase(7)]
        [TestCase(0)]
        public void Bridge_head_is_correct(long headNumber)
        {
            ReadOnlyTxProcessingEnv processingEnv = new(
                new ReadOnlyDbProvider(_dbProvider, false),
                new TrieStoreByPath(_dbProvider.StateDb, LimboLogs.Instance).AsReadOnly(),
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
                new BlocksConfig(),
                false);

            _blockchainBridge.HeadBlock.Should().Be(head);
        }

        [TestCase(true)]
        [TestCase(false)]
        public void GetReceiptAndEffectiveGasPrice_returns_correct_results(bool isCanonical)
        {
            Keccak txHash = TestItem.KeccakA;
            Keccak blockHash = TestItem.KeccakB;
            UInt256 effectiveGasPrice = 123;

            Transaction tx = Build.A.Transaction
                .WithGasPrice(effectiveGasPrice)
                .TestObject;
            Block block = Build.A.Block
                .WithTransactions(tx)
                .TestObject;
            TxReceipt receipt = Build.A.Receipt
                .WithBlockHash(blockHash)
                .WithTransactionHash(txHash)
                .TestObject;

            _blockTree.FindBlock(blockHash, Arg.Is(BlockTreeLookupOptions.RequireCanonical)).Returns(isCanonical ? block : null);
            _blockTree.FindBlock(blockHash, Arg.Is(BlockTreeLookupOptions.TotalDifficultyNotNeeded)).Returns(block);
            _receiptStorage.FindBlockHash(txHash).Returns(blockHash);
            _receiptStorage.Get(block).Returns(new[] { receipt });

            (TxReceipt Receipt, UInt256? EffectiveGasPrice, int LogIndexStart) result = isCanonical ? (receipt, effectiveGasPrice, 0) : (null, null, 0);
            _blockchainBridge.GetReceiptAndEffectiveGasPrice(txHash).Should().BeEquivalentTo(result);
        }
    }
}
