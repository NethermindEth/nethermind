// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Consensus;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Timers;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.State.Witnesses;
using Nethermind.Stats;
using Nethermind.Synchronization.Blocks;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers;
using Nethermind.Synchronization.Reporting;
using Nethermind.Synchronization.SnapSync;
using Nethermind.Trie.Pruning;
using NSubstitute;
using NUnit.Framework;
using BlockTree = Nethermind.Blockchain.BlockTree;

namespace Nethermind.Synchronization.Test
{
    [Parallelizable(ParallelScope.Self)]
    [TestFixture]
    public class OldStyleFullSynchronizerTests
    {
        private readonly TimeSpan _standardTimeoutUnit = TimeSpan.FromMilliseconds(4000);

        [SetUp]
        public async Task Setup()
        {
            _genesisBlock = Build.A.Block.WithNumber(0).TestObject;
            _blockTree = Build.A.BlockTree(_genesisBlock).OfChainLength(1).TestObject;
            IDbProvider dbProvider = await TestMemDbProvider.InitAsync();
            _stateDb = dbProvider.StateDb;
            _codeDb = dbProvider.CodeDb;
            _receiptStorage = Substitute.For<IReceiptStorage>();
            SyncConfig quickConfig = new() { FastSync = false };

            ITimerFactory timerFactory = Substitute.For<ITimerFactory>();
            NodeStatsManager stats = new(timerFactory, LimboLogs.Instance);
            _pool = new SyncPeerPool(_blockTree, stats, new TotalDifficultyBetterPeerStrategy(LimboLogs.Instance), LimboLogs.Instance, 25);
            SyncConfig syncConfig = new();
            ProgressTracker progressTracker = new(_blockTree, dbProvider.StateDb, LimboLogs.Instance);
            SnapProvider snapProvider = new(progressTracker, dbProvider, LimboLogs.Instance);

            TrieStore trieStore = new(_stateDb, LimboLogs.Instance);
            SyncProgressResolver resolver = new(
                _blockTree,
                _receiptStorage,
                _stateDb,
                trieStore,
                progressTracker,
                syncConfig,
                LimboLogs.Instance);
            TotalDifficultyBetterPeerStrategy bestPeerStrategy = new(LimboLogs.Instance);
            MultiSyncModeSelector syncModeSelector = new(resolver, _pool, syncConfig, No.BeaconSync, bestPeerStrategy, LimboLogs.Instance);
            Pivot pivot = new(syncConfig);
            SyncReport syncReport = new(_pool, stats, syncModeSelector, syncConfig, pivot, LimboLogs.Instance);
            BlockDownloaderFactory blockDownloaderFactory = new(
                MainnetSpecProvider.Instance,
                _blockTree,
                _receiptStorage,
                Always.Valid,
                Always.Valid,
                _pool,
                new TotalDifficultyBetterPeerStrategy(LimboLogs.Instance),
                syncReport,
                LimboLogs.Instance);
            _synchronizer = new Synchronizer(
                dbProvider,
                MainnetSpecProvider.Instance,
                _blockTree,
                _receiptStorage,
                _pool,
                stats,
                syncModeSelector,
                syncConfig,
                snapProvider,
                blockDownloaderFactory,
                pivot,
                syncReport,
                LimboLogs.Instance);
            _syncServer = new SyncServer(
                trieStore,
                _codeDb,
                _blockTree,
                _receiptStorage,
                Always.Valid,
                Always.Valid,
                _pool,
                syncModeSelector,
                quickConfig,
                new WitnessCollector(new MemDb(), LimboLogs.Instance),
                Policy.FullGossip,
                MainnetSpecProvider.Instance,
                LimboLogs.Instance);
        }

        [TearDown]
        public async Task TearDown()
        {
            await _pool.StopAsync();
            await _synchronizer.StopAsync();
        }

        private IDb _stateDb;
        private IDb _codeDb;
        private IBlockTree _blockTree;
        private IBlockTree _remoteBlockTree;
        private IReceiptStorage _receiptStorage;
        private Block _genesisBlock;
        private ISyncPeerPool _pool;
        private ISyncServer _syncServer;
        private ISynchronizer _synchronizer;

        [Test, Ignore("travis")]
        public void Retrieves_missing_blocks_in_batches()
        {
            _remoteBlockTree = Build.A.BlockTree(_genesisBlock).OfChainLength(SyncBatchSize.Max * 2).TestObject;
            ISyncPeer peer = new SyncPeerMock(_remoteBlockTree);

            ManualResetEvent resetEvent = new(false);
            _synchronizer.SyncEvent += (sender, args) =>
            {
                if (args.SyncEvent == SyncEvent.Completed || args.SyncEvent == SyncEvent.Failed) resetEvent.Set();
            };
            _pool.Start();
            _synchronizer.Start();
            _pool.AddPeer(peer);

            resetEvent.WaitOne(_standardTimeoutUnit);
            Assert.AreEqual(SyncBatchSize.Max * 2 - 1, (int)_blockTree.BestSuggestedHeader.Number);
        }

        [Test]
        public void Syncs_with_empty_peer()
        {
            _remoteBlockTree = Build.A.BlockTree(_genesisBlock).OfChainLength(1).TestObject;
            ISyncPeer peer = new SyncPeerMock(_remoteBlockTree);

            _pool.Start();
            _synchronizer.Start();
            _pool.AddPeer(peer);

            Assert.AreEqual(0, (int)_blockTree.BestSuggestedHeader.Number);
        }

        [Test]
        public void Syncs_when_knows_more_blocks()
        {
            _blockTree = Build.A.BlockTree(_genesisBlock).OfChainLength(SyncBatchSize.Max * 2).TestObject;
            _remoteBlockTree = Build.A.BlockTree(_genesisBlock).OfChainLength(1).TestObject;
            ISyncPeer peer = new SyncPeerMock(_remoteBlockTree);

            ManualResetEvent resetEvent = new(false);
            _synchronizer.SyncEvent += (sender, args) => { resetEvent.Set(); };
            _pool.Start();
            _synchronizer.Start();
            _pool.AddPeer(peer);

            resetEvent.WaitOne(_standardTimeoutUnit);
            Assert.AreEqual(SyncBatchSize.Max * 2 - 1, (int)_blockTree.BestSuggestedHeader.Number);
        }

        [Test]
        [Ignore("TODO: review this test - failing only with other tests")]
        public void Can_resync_if_missed_a_block()
        {
            _remoteBlockTree = Build.A.BlockTree(_genesisBlock).OfChainLength(SyncBatchSize.Max).TestObject;
            ISyncPeer peer = new SyncPeerMock(_remoteBlockTree);

            SemaphoreSlim semaphore = new(0);
            _synchronizer.SyncEvent += (sender, args) =>
            {
                if (args.SyncEvent == SyncEvent.Completed || args.SyncEvent == SyncEvent.Failed) semaphore.Release(1);
            };
            _pool.Start();
            _synchronizer.Start();
            _pool.AddPeer(peer);

            BlockTreeBuilder.ExtendTree(_remoteBlockTree, SyncBatchSize.Max * 2);
            _syncServer.AddNewBlock(_remoteBlockTree.RetrieveHeadBlock(), peer);

            semaphore.Wait(_standardTimeoutUnit);
            semaphore.Wait(_standardTimeoutUnit);

            Assert.AreEqual(SyncBatchSize.Max * 2 - 1, (int)_blockTree.BestSuggestedHeader.Number);
        }

        [Test, Ignore("travis")]
        public void Can_add_new_block()
        {
            _remoteBlockTree = Build.A.BlockTree(_genesisBlock).OfChainLength(SyncBatchSize.Max).TestObject;
            ISyncPeer peer = new SyncPeerMock(_remoteBlockTree);

            ManualResetEvent resetEvent = new(false);
            _synchronizer.SyncEvent += (sender, args) =>
            {
                if (args.SyncEvent == SyncEvent.Completed || args.SyncEvent == SyncEvent.Failed) resetEvent.Set();
            };

            _pool.Start();
            _synchronizer.Start();
            _pool.AddPeer(peer);

            Block block = Build.A.Block.WithParent(_remoteBlockTree.Head).WithTotalDifficulty((_remoteBlockTree.Head.TotalDifficulty ?? 0) + 1).TestObject;
            _syncServer.AddNewBlock(block, peer);

            resetEvent.WaitOne(_standardTimeoutUnit);

            Assert.AreEqual(SyncBatchSize.Max - 1, (int)_blockTree.BestSuggestedHeader.Number);
        }

        [Test]
        public void Can_sync_on_split_of_length_1()
        {
            BlockTree miner1Tree = Build.A.BlockTree(_genesisBlock).OfChainLength(6).TestObject;
            ISyncPeer miner1 = new SyncPeerMock(miner1Tree);

            ManualResetEvent resetEvent = new(false);
            _synchronizer.SyncEvent += (sender, args) =>
            {
                if (args.SyncEvent == SyncEvent.Completed || args.SyncEvent == SyncEvent.Failed) resetEvent.Set();
            };

            _pool.Start();
            _synchronizer.Start();
            _pool.AddPeer(miner1);

            resetEvent.WaitOne(_standardTimeoutUnit);

            miner1Tree.BestSuggestedHeader.Should().BeEquivalentTo(_blockTree.BestSuggestedHeader, "client agrees with miner before split");

            Block splitBlock = Build.A.Block.WithParent(miner1Tree.FindParent(miner1Tree.Head, BlockTreeLookupOptions.TotalDifficultyNotNeeded)).WithDifficulty(miner1Tree.Head.Difficulty - 1).TestObject;
            Block splitBlockChild = Build.A.Block.WithParent(splitBlock).TestObject;

            miner1Tree.SuggestBlock(splitBlock);
            miner1Tree.UpdateMainChain(splitBlock);
            miner1Tree.SuggestBlock(splitBlockChild);
            miner1Tree.UpdateMainChain(splitBlockChild);

            splitBlockChild.Header.Should().BeEquivalentTo(miner1Tree.BestSuggestedHeader, "split as expected");

            resetEvent.Reset();

            _syncServer.AddNewBlock(splitBlockChild, miner1);

            resetEvent.WaitOne(_standardTimeoutUnit);

            Assert.AreEqual(miner1Tree.BestSuggestedHeader.Hash, _blockTree.BestSuggestedHeader.Hash, "client agrees with miner after split");
        }

        [Test]
        public void Can_sync_on_split_of_length_6()
        {
            BlockTree miner1Tree = Build.A.BlockTree(_genesisBlock).OfChainLength(6).TestObject;
            ISyncPeer miner1 = new SyncPeerMock(miner1Tree);

            ManualResetEvent resetEvent = new(false);
            _synchronizer.SyncEvent += (sender, args) =>
            {
                if (args.SyncEvent == SyncEvent.Completed || args.SyncEvent == SyncEvent.Failed) resetEvent.Set();
            };

            _pool.Start();
            _synchronizer.Start();
            _pool.AddPeer(miner1);

            resetEvent.WaitOne(_standardTimeoutUnit);

            Assert.AreEqual(miner1Tree.BestSuggestedHeader.Hash, _blockTree.BestSuggestedHeader.Hash, "client agrees with miner before split");

            miner1Tree.AddBranch(7, 0, 1);

            Assert.AreNotEqual(miner1Tree.BestSuggestedHeader.Hash, _blockTree.BestSuggestedHeader.Hash, "client does not agree with miner after split");

            resetEvent.Reset();

            _syncServer.AddNewBlock(miner1Tree.RetrieveHeadBlock(), miner1);

            resetEvent.WaitOne(_standardTimeoutUnit);

            Assert.AreEqual(miner1Tree.BestSuggestedHeader.Hash, _blockTree.BestSuggestedHeader.Hash, "client agrees with miner after split");
        }

        [Test]
        [Ignore("Review sync manager tests")]
        public async Task Does_not_do_full_sync_when_not_needed()
        {
            BlockTree minerTree = Build.A.BlockTree(_genesisBlock).OfChainLength(6).TestObject;
            ISyncPeer miner1 = new SyncPeerMock(minerTree);

            AutoResetEvent resetEvent = new(false);
            _synchronizer.SyncEvent += (sender, args) =>
            {
                if (args.SyncEvent == SyncEvent.Completed || args.SyncEvent == SyncEvent.Failed) resetEvent.Set();
            };

            _pool.Start();
            _synchronizer.Start();
            _pool.AddPeer(miner1);
            resetEvent.WaitOne(_standardTimeoutUnit);

            Assert.AreEqual(minerTree.BestSuggestedHeader.Hash, _blockTree.BestSuggestedHeader.Hash, "client agrees with miner before split");

            Block newBlock = Build.A.Block.WithParent(minerTree.Head).TestObject;
            minerTree.SuggestBlock(newBlock);
            minerTree.UpdateMainChain(newBlock);

            ISyncPeer miner2 = Substitute.For<ISyncPeer>();
            miner2.GetHeadBlockHeader(Arg.Any<Keccak>(), Arg.Any<CancellationToken>()).Returns(miner1.GetHeadBlockHeader(null, CancellationToken.None));
            miner2.Node.Id.Returns(TestItem.PublicKeyB);

            Assert.AreEqual(newBlock.Number, await miner2.GetHeadBlockHeader(null, Arg.Any<CancellationToken>()), "number as expected");

            _pool.Start();
            _synchronizer.Start();
            _pool.AddPeer(miner2);
            resetEvent.WaitOne(_standardTimeoutUnit);

            await miner2.Received().GetBlockHeaders(6, 1, 0, default);
        }

        [Test]
        [Ignore("Review sync manager tests")]
        public async Task Does_not_do_full_sync_when_not_needed_with_split()
        {
            BlockTree minerTree = Build.A.BlockTree(_genesisBlock).OfChainLength(6).TestObject;
            ISyncPeer miner1 = new SyncPeerMock(minerTree);

            AutoResetEvent resetEvent = new(false);
            _synchronizer.SyncEvent += (sender, args) =>
            {
                if (args.SyncEvent == SyncEvent.Completed || args.SyncEvent == SyncEvent.Failed) resetEvent.Set();
            };

            _pool.Start();
            _synchronizer.Start();
            _pool.AddPeer(miner1);
            resetEvent.WaitOne(_standardTimeoutUnit);

            Assert.AreEqual(minerTree.BestSuggestedHeader.Hash, _blockTree.BestSuggestedHeader.Hash, "client agrees with miner before split");

            Block newBlock = Build.A.Block.WithParent(minerTree.Head).TestObject;
            minerTree.SuggestBlock(newBlock);
            minerTree.UpdateMainChain(newBlock);

            ISyncPeer miner2 = Substitute.For<ISyncPeer>();
            miner2.GetHeadBlockHeader(Arg.Any<Keccak>(), Arg.Any<CancellationToken>()).Returns(miner1.GetHeadBlockHeader(null, CancellationToken.None));
            miner2.Node.Id.Returns(TestItem.PublicKeyB);

            Assert.AreEqual(newBlock.Number, await miner2.GetHeadBlockHeader(null, Arg.Any<CancellationToken>()), "number as expected");

            _pool.Start();
            _synchronizer.Start();
            _pool.AddPeer(miner2);
            resetEvent.WaitOne(_standardTimeoutUnit);

            await miner2.Received().GetBlockHeaders(6, 1, 0, default);
        }

        [Test]
        public void Can_retrieve_node_values()
        {
            _stateDb.Set(TestItem.KeccakA, TestItem.RandomDataA);
            byte[][] values = _syncServer.GetNodeData(new[] { TestItem.KeccakA, TestItem.KeccakB });
            Assert.AreEqual(2, values.Length, "data.Length");
            Assert.AreEqual(TestItem.RandomDataA, values[0], "data[0]");
            Assert.AreEqual(null, values[1], "data[1]");
        }

        [Test]
        public void Can_retrieve_empty_receipts()
        {
            _blockTree = Build.A.BlockTree(_genesisBlock).OfChainLength(2).TestObject;
            Block block0 = _blockTree.FindBlock(0, BlockTreeLookupOptions.None);
            Block block1 = _blockTree.FindBlock(1, BlockTreeLookupOptions.None);

            _syncServer.GetReceipts(block0.Hash).Should().HaveCount(0);
            _syncServer.GetReceipts(block1.Hash).Should().HaveCount(0);
            _syncServer.GetReceipts(TestItem.KeccakA).Should().HaveCount(0);
        }
    }
}
