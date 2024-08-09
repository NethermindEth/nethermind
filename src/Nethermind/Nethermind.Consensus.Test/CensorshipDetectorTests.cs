// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus.Processing;
using Nethermind.TxPool;
using Nethermind.Logging;
using NSubstitute;
using NUnit.Framework;
using Nethermind.Blockchain;
using Nethermind.Core;
using System.Collections.Generic;
using Nethermind.Core.Specs;
using Nethermind.Consensus.Comparers;
using Nethermind.Specs;
using Nethermind.Consensus.Validators;
using Nethermind.Crypto;
using Nethermind.State;
using Nethermind.Trie.Pruning;
using Nethermind.Db;
using FluentAssertions;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Extensions;
using Nethermind.Specs.Forks;

namespace Nethermind.Consensus.Test
{
    [TestFixture]
    public class CensorshipDetectorTests
    {
        private ILogManager _logManager;
        private WorldState _stateProvider;
        private IBlockProcessor _blockProcessor;
        private IBlockTree _blockTree;
        private ISpecProvider _specProvider;
        private IEthereumEcdsa _ethereumEcdsa;
        private IComparer<Transaction> _comparer;
        private TxPool.TxPool _txPool;
        private CensorshipDetector _censorshipDetector;

        [SetUp]
        public void Setup()
        {
            _logManager = LimboLogs.Instance;
            var trieStore = new TrieStore(new MemDb(), _logManager);
            var codeDb = new MemDb();
            _stateProvider = new WorldState(trieStore, codeDb, _logManager);
            _blockProcessor = Substitute.For<IBlockProcessor>();
            _blockTree = Substitute.For<IBlockTree>();
            _blockTree.FindBestSuggestedHeader().Returns(Build.A.BlockHeader.WithNumber(1_000_000).TestObject);
        }

        [TearDown]
        public void TearDown()
        {
            _txPool.Dispose();
            _censorshipDetector.Dispose();
        }

        [Test]
        public void Block_will_get_added_to_temporary_cache_on_block_processing()
        {
            _txPool = CreatePool();
            _censorshipDetector = new(_txPool, _comparer, _blockProcessor, _blockTree, _logManager);

            Block block = Build.A.Block.WithNumber(0).TestObject;
            _blockProcessor.BlockProcessing += Raise.EventWith(new BlockEventArgs(block));

            Assert.That(() => _censorshipDetector.TemporaryCacheContainsBlock(0), Is.EqualTo(true).After(10, 1));
        }

        [Test]
        public void Unprocessed_blocks_will_not_get_added_to_temporary_cache()
        {
            _txPool = CreatePool();
            _censorshipDetector = new(_txPool, _comparer, _blockProcessor, _blockTree, _logManager);
            Assert.That(() => _censorshipDetector.TemporaryCacheContainsBlock(0), Is.EqualTo(false).After(10, 1));
        }

        /* First, all blocks will get added and then all will be retrieved to show multiple blocks were added correctly */
        [Test]
        public void Multiple_blocks_will_get_added_to_temporary_cache_on_block_processing()
        {
            _txPool = CreatePool();
            _censorshipDetector = new(_txPool, _comparer, _blockProcessor, _blockTree, _logManager);

            for (long i = 0; i < 5; i++)
            {
                Block block = Build.A.Block.WithNumber(i).TestObject;
                _blockProcessor.BlockProcessing += Raise.EventWith(new BlockEventArgs(block));
            }

            for (long i = 0; i < 5; i++)
            {
                Assert.That(() => _censorshipDetector.TemporaryCacheContainsBlock(i), Is.EqualTo(true).After(10, 1));
            }
        }

        [Test]
        public void Temporary_cache_will_not_exceed_capacity()
        {
            _txPool = CreatePool();
            _censorshipDetector = new(_txPool, _comparer, _blockProcessor, _blockTree, _logManager);

            for (long i = 0; i < 16; i++)
            {
                Block block = Build.A.Block.WithNumber(i).TestObject;
                _blockProcessor.BlockProcessing += Raise.EventWith(new BlockEventArgs(block));
            }

            Assert.That(() => _censorshipDetector.TemporaryCacheContainsBlock(0), Is.EqualTo(true).After(50, 1));

            Block lastBlock = Build.A.Block.WithNumber(16).TestObject;
            _blockProcessor.BlockProcessing += Raise.EventWith(new BlockEventArgs(lastBlock));

            Assert.That(() => _censorshipDetector.TemporaryCacheContainsBlock(0), Is.EqualTo(false).After(50, 1));
        }

        [Test]
        public void Block_will_get_added_to_main_cache_when_block_is_correctly_added_to_main()
        {
            _txPool = CreatePool();
            _censorshipDetector = new(_txPool, _comparer, _blockProcessor, _blockTree, _logManager);

            Block block = Build.A.Block.WithNumber(0).TestObject;
            _blockProcessor.BlockProcessing += Raise.EventWith(new BlockEventArgs(block));

            Assert.That(() => _censorshipDetector.TemporaryCacheContainsBlock(0), Is.EqualTo(true).After(10, 1));

            _blockTree.BlockAddedToMain += Raise.EventWith(new BlockReplacementEventArgs(block));
            Assert.That(() => _censorshipDetector.CacheContainsBlock(0), Is.EqualTo(true).After(10, 1));
        }

        [Test]
        public void Block_will_not_get_added_to_main_cache_if_not_present_in_temporary_cache()
        {
            _txPool = CreatePool();
            _censorshipDetector = new(_txPool, _comparer, _blockProcessor, _blockTree, _logManager);

            Block block = Build.A.Block.WithNumber(0).TestObject;

            _blockTree.BlockAddedToMain += Raise.EventWith(new BlockReplacementEventArgs(block));
            Assert.That(() => _censorshipDetector.CacheContainsBlock(0), Is.EqualTo(false).After(10, 1));
        }

        /* First, all blocks will get added and then all will be retrieved to show multiple blocks were added correctly */
        [Test]
        public void Multiple_blocks_will_get_added_to_main_cache_when_blocks_are_correctly_added_to_main()
        {
            _txPool = CreatePool();
            _censorshipDetector = new(_txPool, _comparer, _blockProcessor, _blockTree, _logManager);

            for (long i = 0; i < 4; i++)
            {
                Block block = Build.A.Block.WithNumber(i).TestObject;
                _blockProcessor.BlockProcessing += Raise.EventWith(new BlockEventArgs(block));
                Assert.That(() => _censorshipDetector.TemporaryCacheContainsBlock(i), Is.EqualTo(true).After(10, 1));
                _blockTree.BlockAddedToMain += Raise.EventWith(new BlockReplacementEventArgs(block));
            }

            for (long i = 0; i < 4; i++)
            {
                Assert.That(() => _censorshipDetector.CacheContainsBlock(i), Is.EqualTo(true).After(10, 1));
            }
        }

        [Test]
        public void Main_cache_will_not_exceed_capacity()
        {
            _txPool = CreatePool();
            _censorshipDetector = new(_txPool, _comparer, _blockProcessor, _blockTree, _logManager);

            for (long i = 0; i < 4; i++)
            {
                Block block = Build.A.Block.WithNumber(i).TestObject;
                _blockProcessor.BlockProcessing += Raise.EventWith(new BlockEventArgs(block));
                Assert.That(() => _censorshipDetector.TemporaryCacheContainsBlock(i), Is.EqualTo(true).After(10, 1));
                _blockTree.BlockAddedToMain += Raise.EventWith(new BlockReplacementEventArgs(block));
            }

            Assert.That(() => _censorshipDetector.CacheContainsBlock(0), Is.EqualTo(true).After(10, 1));

            Block lastBlock = Build.A.Block.WithNumber(4).TestObject;
            _blockProcessor.BlockProcessing += Raise.EventWith(new BlockEventArgs(lastBlock));
            Assert.That(() => _censorshipDetector.TemporaryCacheContainsBlock(4), Is.EqualTo(true).After(10, 1));
            _blockTree.BlockAddedToMain += Raise.EventWith(new BlockReplacementEventArgs(lastBlock));

            Assert.That(() => _censorshipDetector.CacheContainsBlock(0), Is.EqualTo(false).After(10, 1));
        }

        [Test]
        public void Potential_censorship_will_be_false_if_tx_pool_is_empty()
        {
            _txPool = CreatePool();
            _censorshipDetector = new(_txPool, _comparer, _blockProcessor, _blockTree, _logManager);

            Transaction tx = Build.A.Transaction.
                            WithType(TxType.EIP1559).
                            WithMaxFeePerGas(10.Wei()).
                            WithMaxPriorityFeePerGas(1.Wei()).
                            SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).
                            TestObject;
            CreateSenderAccount(tx);

            Block block = Build.A.Block.WithNumber(0).WithBaseFeePerGas(5.Wei()).WithTransactions([tx]).TestObject;
            CensorshipDetectorAssertions(block, false);
        }

        /* Tx Pool to be filled here */
        [Test]
        public void Potential_censorship_will_be_true_if_best_tx_in_pool_is_not_included_in_block()
        {
            _txPool = CreatePool();
            _censorshipDetector = new(_txPool, _comparer, _blockProcessor, _blockTree, _logManager);

            Transaction tx1 = SubmitTxToPool(1, TestItem.PrivateKeyA);
            SubmitTxToPool(2, TestItem.PrivateKeyB);

            Block block = Build.A.Block.WithNumber(0).WithBaseFeePerGas(5.Wei()).WithTransactions([tx1]).TestObject;
            CensorshipDetectorAssertions(block, true);
        }

        [Test]
        public void Potential_censorship_will_be_false_if_best_tx_in_pool_is_included_in_block()
        {
            _txPool = CreatePool();
            _censorshipDetector = new(_txPool, _comparer, _blockProcessor, _blockTree, _logManager);

            Transaction tx1 = SubmitTxToPool(1, TestItem.PrivateKeyA);
            Transaction tx2 = SubmitTxToPool(2, TestItem.PrivateKeyB);

            Block block = Build.A.Block.WithNumber(0).WithBaseFeePerGas(5.Wei()).WithTransactions([tx1, tx2]).TestObject;
            CensorshipDetectorAssertions(block, false);
        }

        [Test]
        public void Censorship_is_not_detected_when_potential_censorship_is_false_for_some_blocks_in_main_cache()
        {
            _txPool = CreatePool();
            _censorshipDetector = new(_txPool, _comparer, _blockProcessor, _blockTree, _logManager);

            Transaction tx1 = SubmitTxToPool(1, TestItem.PrivateKeyA);
            Transaction tx2 = SubmitTxToPool(2, TestItem.PrivateKeyB);
            Transaction tx3 = SubmitTxToPool(3, TestItem.PrivateKeyC);
            Transaction tx4 = SubmitTxToPool(4, TestItem.PrivateKeyD);

            Block block1 = Build.A.Block.WithNumber(1).WithBaseFeePerGas(5.Wei()).WithTransactions([tx4]).TestObject;
            CensorshipDetectorAssertions(block1, false);
            _txPool.RemoveTransaction(tx4.Hash);
            Assert.That(() => _txPool.GetBestTx().MaxPriorityFeePerGas, Is.EqualTo(3.Wei()).After(10, 1));

            Transaction tx5 = SubmitTxToPool(5, TestItem.PrivateKeyE);

            Block block2 = Build.A.Block.WithNumber(2).WithBaseFeePerGas(5.Wei()).WithTransactions([tx3]).TestObject;
            CensorshipDetectorAssertions(block2, true);
            _txPool.RemoveTransaction(tx5.Hash);
            _txPool.RemoveTransaction(tx3.Hash);
            Assert.That(() => _txPool.GetBestTx().MaxPriorityFeePerGas, Is.EqualTo(2.Wei()).After(10, 1));

            Block block3 = Build.A.Block.WithNumber(3).WithBaseFeePerGas(5.Wei()).WithTransactions([tx2]).TestObject;
            CensorshipDetectorAssertions(block3, false);
            _txPool.RemoveTransaction(tx2.Hash);
            Assert.That(() => _txPool.GetBestTx().MaxPriorityFeePerGas, Is.EqualTo(1.Wei()).After(10, 1));

            Block block4 = Build.A.Block.WithNumber(4).WithBaseFeePerGas(5.Wei()).WithTransactions([tx1]).TestObject;
            CensorshipDetectorAssertions(block4, false);
            _txPool.RemoveTransaction(tx1.Hash);
            Assert.That(() => _txPool.GetBestTx().MaxPriorityFeePerGas, Is.EqualTo(0.Wei()).After(10, 1));

            Assert.That(() => _censorshipDetector.CensorshipDetected, Is.EqualTo(false).After(10, 1));
        }

        [Test]
        public void Censorship_is_detected_when_potential_censorship_is_true_for_all_blocks_in_main_cache()
        {
            _txPool = CreatePool();
            _censorshipDetector = new(_txPool, _comparer, _blockProcessor, _blockTree, _logManager);

            Transaction tx1 = SubmitTxToPool(1, TestItem.PrivateKeyA);
            Transaction tx2 = SubmitTxToPool(2, TestItem.PrivateKeyB);
            Transaction tx3 = SubmitTxToPool(3, TestItem.PrivateKeyC);
            Transaction tx4 = SubmitTxToPool(4, TestItem.PrivateKeyD);
            Transaction tx5 = SubmitTxToPool(5, TestItem.PrivateKeyE);

            Block block1 = Build.A.Block.WithNumber(1).WithBaseFeePerGas(5.Wei()).WithTransactions([tx4]).TestObject;
            CensorshipDetectorAssertions(block1, true);
            _txPool.RemoveTransaction(tx4.Hash);

            Block block2 = Build.A.Block.WithNumber(2).WithBaseFeePerGas(5.Wei()).WithTransactions([tx3]).TestObject;
            CensorshipDetectorAssertions(block2, true);
            _txPool.RemoveTransaction(tx3.Hash);

            Block block3 = Build.A.Block.WithNumber(3).WithBaseFeePerGas(5.Wei()).WithTransactions([tx2]).TestObject;
            CensorshipDetectorAssertions(block3, true);
            _txPool.RemoveTransaction(tx2.Hash);

            Block block4 = Build.A.Block.WithNumber(4).WithBaseFeePerGas(5.Wei()).WithTransactions([tx1]).TestObject;
            CensorshipDetectorAssertions(block4, true);
            _txPool.RemoveTransaction(tx1.Hash);

            Assert.That(() => _censorshipDetector.CensorshipDetected, Is.EqualTo(true).After(10, 1));
        }

        private ChainHeadInfoProvider _headInfo;

        private TxPool.TxPool CreatePool(
            ITxPoolConfig config = null,
            ChainHeadInfoProvider chainHeadInfoProvider = null,
            IBlobTxStorage txStorage = null,
            bool eip1559Enabled = true)
        {
            if (eip1559Enabled)
            {
                _specProvider = Substitute.For<ISpecProvider>();
                _specProvider.GetSpec(Arg.Any<BlockHeader>()).Returns(London.Instance);
            }
            else
            {
                _specProvider = MainnetSpecProvider.Instance;
            }

            _ethereumEcdsa = new EthereumEcdsa(_specProvider.ChainId, _logManager);
            _comparer = new TransactionComparerProvider(_specProvider, _blockTree).GetDefaultComparer();

            txStorage ??= new BlobTxStorage();
            _headInfo = chainHeadInfoProvider;
            _headInfo ??= new(_specProvider, _blockTree, _stateProvider);

            return new(
                _ethereumEcdsa,
                txStorage,
                _headInfo,
                config ?? new TxPoolConfig(),
                new TxValidator(_specProvider.ChainId),
                _logManager,
                _comparer);
        }

        private void CreateSenderAccount(Transaction tx, int amount = 1_000_000)
        {
            _stateProvider.CreateAccount(tx.SenderAddress, amount.Wei());
        }

        private void RemoveTxsFromPool(Block block)
        {
            foreach (Transaction tx in block.Transactions)
            {
                _txPool.RemoveTransaction(tx.Hash);
            }
        }

        private void CensorshipDetectorAssertions(Block block, bool expectedCensorshipStatus)
        {
            _blockProcessor.BlockProcessing += Raise.EventWith(new BlockEventArgs(block));
            Assert.That(() => _censorshipDetector.GetTemporaryCensorshipStatus(block.Number),
            Is.EqualTo(expectedCensorshipStatus).After(50, 1));

            RemoveTxsFromPool(block);

            _blockTree.BlockAddedToMain += Raise.EventWith(new BlockReplacementEventArgs(block));
            Assert.That(() => _censorshipDetector.GetCensorshipStatus(block.Number),
            Is.EqualTo(expectedCensorshipStatus).After(50, 1));
        }

        private Transaction SubmitTxToPool(int maxPriorityFeePerGas, PrivateKey privateKey)
        {
            Transaction tx = Build.A.Transaction.
                            WithType(TxType.EIP1559).
                            WithMaxFeePerGas(10.Wei()).
                            WithMaxPriorityFeePerGas(maxPriorityFeePerGas.Wei()).
                            SignedAndResolved(_ethereumEcdsa, privateKey).
                            TestObject;
            CreateSenderAccount(tx);
            AcceptTxResult result = _txPool.SubmitTx(tx, TxHandlingOptions.PersistentBroadcast);
            result.Should().Be(AcceptTxResult.Accepted);
            return tx;
        }
    }
}
