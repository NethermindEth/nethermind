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
using Nethermind.Evm;
using Nethermind.Facade.Simulate;
using Nethermind.State;

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

        private class TestReadOnlyTxProcessingEnv : ReadOnlyTxProcessingEnv
        {
            public TestReadOnlyTxProcessingEnv(
                IWorldStateManager worldStateManager,
                IBlockTree blockTree,
                ISpecProvider specProvider,
                ILogManager logManager,
                ITransactionProcessor transactionProcessor) :
                base(worldStateManager, blockTree, specProvider, logManager)
            {
                _transactionProcessor = transactionProcessor;
            }
        }

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

            ReadOnlyDbProvider dbProvider = new ReadOnlyDbProvider(_dbProvider, false);
            IReadOnlyTrieStore trieStore = new TrieStore(_dbProvider.StateDb, LimboLogs.Instance).AsReadOnly();

            IWorldStateManager readOnlyWorldStateManager =
                new ReadOnlyWorldStateManager(dbProvider, trieStore, LimboLogs.Instance);

            IReadOnlyBlockTree readOnlyBlockTree = _blockTree.AsReadOnly();
            ReadOnlyTxProcessingEnv processingEnv = new TestReadOnlyTxProcessingEnv(
                readOnlyWorldStateManager,
                readOnlyBlockTree,
                _specProvider,
                LimboLogs.Instance,
                _transactionProcessor);

            SimulateReadOnlyBlocksProcessingEnvFactory simulateProcessingEnvFactory = new SimulateReadOnlyBlocksProcessingEnvFactory(
                readOnlyWorldStateManager,
                readOnlyBlockTree,
                new ReadOnlyDbProvider(_dbProvider, true),
                _specProvider,
                LimboLogs.Instance);

            _blockchainBridge = new BlockchainBridge(
                processingEnv,
                simulateProcessingEnvFactory,
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
                Arg.Is<BlockExecutionContext>(blkCtx =>
                    blkCtx.Header.IsPostMerge && blkCtx.Header.Random == TestItem.KeccakA),
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
                Arg.Is<BlockExecutionContext>(blkCtx => blkCtx.Header.Number == 10),
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
                Arg.Is<BlockExecutionContext>(blkCtx => blkCtx.Header.MixHash == TestItem.KeccakA),
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
                Arg.Is<BlockExecutionContext>(blkCtx => blkCtx.Header.Beneficiary == TestItem.AddressB),
                Arg.Any<ITxTracer>());
        }

        [TestCase(7)]
        [TestCase(0)]
        public void Bridge_head_is_correct(long headNumber)
        {
            ReadOnlyDbProvider dbProvider = new ReadOnlyDbProvider(_dbProvider, false);
            IReadOnlyTrieStore trieStore = new TrieStore(_dbProvider.StateDb, LimboLogs.Instance).AsReadOnly();

            IWorldStateManager readOnlyWorldStateManager =
                new ReadOnlyWorldStateManager(dbProvider, trieStore, LimboLogs.Instance);
            IReadOnlyBlockTree roBlockTree = _blockTree.AsReadOnly();
            ReadOnlyTxProcessingEnv processingEnv = new(
                readOnlyWorldStateManager,
                roBlockTree,
                _specProvider,
                LimboLogs.Instance);

            SimulateReadOnlyBlocksProcessingEnvFactory simulateProcessingEnv = new SimulateReadOnlyBlocksProcessingEnvFactory(
                readOnlyWorldStateManager,
                roBlockTree,
                new ReadOnlyDbProvider(_dbProvider, true),
                _specProvider,
                LimboLogs.Instance);

            Block head = Build.A.Block.WithNumber(headNumber).TestObject;
            Block bestSuggested = Build.A.Block.WithNumber(8).TestObject;

            _blockTree.Head.Returns(head);
            _blockTree.BestSuggestedBody.Returns(bestSuggested);

            _blockchainBridge = new BlockchainBridge(
                processingEnv,
                simulateProcessingEnv,
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

        [TestCase(true, true)]
        [TestCase(false, true)]
        [TestCase(true, false)]
        [TestCase(false, false)]
        public void GetReceiptAndGasInfo_returns_correct_results(bool isCanonical, bool postEip4844)
        {
            Hash256 txHash = TestItem.KeccakA;
            Hash256 blockHash = TestItem.KeccakB;
            UInt256 effectiveGasPrice = 123;

            Transaction tx = postEip4844
                ? Build.A.Transaction
                    .WithGasPrice(effectiveGasPrice)
                    .WithType(TxType.Blob)
                    .WithMaxFeePerBlobGas(2)
                    .WithBlobVersionedHashes(2)
                    .TestObject
                : Build.A.Transaction
                    .WithGasPrice(effectiveGasPrice)
                    .TestObject;
            Block block = postEip4844
                ? Build.A.Block
                    .WithTransactions(tx)
                    .WithExcessBlobGas(2)
                    .TestObject
                : Build.A.Block
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

            (TxReceipt? Receipt, TxGasInfo? GasInfo, int LogIndexStart) result = postEip4844
                ? (receipt, new(effectiveGasPrice, 1, 262144), 0)
                : (receipt, new(effectiveGasPrice), 0);

            if (!isCanonical)
            {
                result = (null, null, 0);
            }

            _blockchainBridge.GetReceiptAndGasInfo(txHash).Should().BeEquivalentTo(result);
        }
    }
}
