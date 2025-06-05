// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using FluentAssertions;
using Nethermind.Api;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Config;
using Nethermind.Consensus;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Modules;
using Nethermind.Db;
using Nethermind.Synchronization.Peers;
using NSubstitute;
using NUnit.Framework;
using BlockTree = Nethermind.Blockchain.BlockTree;

namespace Nethermind.Synchronization.Test
{
    [Parallelizable(ParallelScope.Self)]
    [TestFixture]
    public class OldStyleFullSynchronizerTests
    {
        private const int SyncBatchSizeMax = 128;

        private readonly TimeSpan _standardTimeoutUnit = TimeSpan.FromMilliseconds(4000);

        [SetUp]
        public void Setup()
        {
            _genesisBlock = Build.A.Block.WithNumber(0).TestObject;
            _blockTree = Build.A.BlockTree(_genesisBlock).OfChainLength(1).TestObject;
            ConfigProvider configProvider = new ConfigProvider();
            ISyncConfig syncConfig = configProvider.GetConfig<ISyncConfig>();
            syncConfig.FastSync = false;

            IInitConfig initConfig = configProvider.GetConfig<IInitConfig>();
            initConfig.StateDbKeyScheme = INodeStorage.KeyScheme.Hash;

            IPruningConfig pruningConfig = configProvider.GetConfig<IPruningConfig>();
            pruningConfig.Mode = PruningMode.Full;

            IContainer container = new ContainerBuilder()
                .AddModule(new TestNethermindModule(configProvider))
                .AddSingleton<IBlockTree>(_blockTree)
                .AddSingleton<IBlockValidator>(Always.Valid)
                .AddSingleton<ISealValidator>(Always.Valid)
                .Build();
            _container = container;
        }

        [TearDown]
        public async Task TearDown()
        {
            await _container.DisposeAsync();
        }

        private IDb _stateDb => _container.Resolve<IDbProvider>().StateDb;
        private IBlockTree _blockTree = null!;
        private IBlockTree _remoteBlockTree = null!;
        private Block _genesisBlock = null!;
        private ISyncPeerPool SyncPeerPool => _container.Resolve<ISyncPeerPool>();
        private ISyncServer SyncServer => _container.Resolve<ISyncServer>();
        private ISynchronizer Synchronizer => _container.Resolve<ISynchronizer>()!;
        private IContainer _container;

        [Test, Ignore("travis")]
        public void Retrieves_missing_blocks_in_batches()
        {
            _remoteBlockTree = Build.A.BlockTree(_genesisBlock).OfChainLength(SyncBatchSizeMax * 2).TestObject;
            ISyncPeer peer = new SyncPeerMock(_remoteBlockTree);

            ManualResetEvent resetEvent = new(false);
            Synchronizer.SyncEvent += (_, args) =>
            {
                if (args.SyncEvent == SyncEvent.Completed || args.SyncEvent == SyncEvent.Failed) resetEvent.Set();
            };
            SyncPeerPool.Start();
            Synchronizer.Start();
            SyncPeerPool.AddPeer(peer);

            resetEvent.WaitOne(_standardTimeoutUnit);
            Assert.That(_blockTree.BestSuggestedHeader!.Number, Is.EqualTo(SyncBatchSizeMax * 2 - 1));
        }

        [Test]
        public void Syncs_with_empty_peer()
        {
            _remoteBlockTree = Build.A.BlockTree(_genesisBlock).OfChainLength(1).TestObject;
            ISyncPeer peer = new SyncPeerMock(_remoteBlockTree);

            SyncPeerPool.Start();
            Synchronizer.Start();
            SyncPeerPool.AddPeer(peer);

            Assert.That(_blockTree.BestSuggestedHeader!.Number, Is.EqualTo(0));
        }

        [Test]
        public void Syncs_when_knows_more_blocks()
        {
            _blockTree = Build.A.BlockTree(_genesisBlock).OfChainLength(SyncBatchSizeMax * 2).TestObject;
            _remoteBlockTree = Build.A.BlockTree(_genesisBlock).OfChainLength(2).TestObject;
            _remoteBlockTree.Head?.Number.Should().NotBe(0);
            ISyncPeer peer = new SyncPeerMock(_remoteBlockTree);

            ManualResetEvent resetEvent = new(false);
            Synchronizer.SyncEvent += (_, _) => { resetEvent.Set(); };
            SyncPeerPool.Start();
            Synchronizer.Start();
            SyncPeerPool.AddPeer(peer);

            resetEvent.WaitOne(_standardTimeoutUnit);
            Assert.That(_blockTree.BestSuggestedHeader!.Number, Is.EqualTo(SyncBatchSizeMax * 2 - 1));
        }

        [Test]
        [Ignore("TODO: review this test - failing only with other tests")]
        public void Can_resync_if_missed_a_block()
        {
            _remoteBlockTree = Build.A.BlockTree(_genesisBlock).OfChainLength(SyncBatchSizeMax).TestObject;
            ISyncPeer peer = new SyncPeerMock(_remoteBlockTree);

            SemaphoreSlim semaphore = new(0);
            Synchronizer.SyncEvent += (_, args) =>
            {
                if (args.SyncEvent == SyncEvent.Completed || args.SyncEvent == SyncEvent.Failed) semaphore.Release(1);
            };
            SyncPeerPool.Start();
            Synchronizer.Start();
            SyncPeerPool.AddPeer(peer);

            BlockTreeBuilder.ExtendTree(_remoteBlockTree, SyncBatchSizeMax * 2);
            SyncServer.AddNewBlock(_remoteBlockTree.RetrieveHeadBlock()!, peer);

            semaphore.Wait(_standardTimeoutUnit);
            semaphore.Wait(_standardTimeoutUnit);

            Assert.That(_blockTree.BestSuggestedHeader!.Number, Is.EqualTo(SyncBatchSizeMax * 2 - 1));
        }

        [Test, Ignore("travis")]
        public void Can_add_new_block()
        {
            _remoteBlockTree = Build.A
                .BlockTree(_genesisBlock)
                .OfChainLength(SyncBatchSizeMax).TestObject;
            ISyncPeer peer = new SyncPeerMock(_remoteBlockTree);

            ManualResetEvent resetEvent = new(false);
            Synchronizer.SyncEvent += (_, args) =>
            {
                if (args.SyncEvent == SyncEvent.Completed || args.SyncEvent == SyncEvent.Failed) resetEvent.Set();
            };

            SyncPeerPool.Start();
            Synchronizer.Start();
            SyncPeerPool.AddPeer(peer);

            Block block = Build.A.Block
                .WithParent(_remoteBlockTree.Head!)
                .WithTotalDifficulty((_remoteBlockTree.Head!.TotalDifficulty ?? 0) + 1)
                .TestObject;
            SyncServer.AddNewBlock(block, peer);

            resetEvent.WaitOne(_standardTimeoutUnit);

            Assert.That(_blockTree.BestSuggestedHeader!.Number, Is.EqualTo(SyncBatchSizeMax - 1));
        }

        [Test]
        public void Can_sync_on_split_of_length_1()
        {
            BlockTree miner1Tree = Build.A.BlockTree(_genesisBlock).OfChainLength(6).TestObject;
            ISyncPeer miner1 = new SyncPeerMock(miner1Tree);

            SyncPeerPool.Start();
            Synchronizer.Start();
            SyncPeerPool.AddPeer(miner1);

            Assert.That(() => _blockTree.BestSuggestedHeader?.Number, Is.EqualTo(miner1Tree.BestSuggestedHeader!.Number).After((int)_standardTimeoutUnit.TotalMilliseconds, 100));
            miner1Tree.BestSuggestedHeader.Should().BeEquivalentTo(_blockTree.BestSuggestedHeader, options => options.Excluding(h => h!.MaybeParent), "client agrees with miner before split");

            Block splitBlock = Build.A.Block
                .WithParent(miner1Tree.FindParent(miner1Tree.Head!, BlockTreeLookupOptions.TotalDifficultyNotNeeded)!)
                .WithDifficulty(miner1Tree.Head!.Difficulty - 1)
                .TestObject;
            Block splitBlockChild = Build.A.Block.WithParent(splitBlock).TestObject;

            miner1Tree.SuggestBlock(splitBlock);
            miner1Tree.UpdateMainChain(splitBlock);
            miner1Tree.SuggestBlock(splitBlockChild);
            miner1Tree.UpdateMainChain(splitBlockChild);

            splitBlockChild.Header.Should().BeEquivalentTo(miner1Tree.BestSuggestedHeader, "split as expected");

            SyncServer.AddNewBlock(splitBlockChild, miner1);

            Assert.That(() => _blockTree.BestSuggestedHeader?.Number, Is.EqualTo(miner1Tree.BestSuggestedHeader!.Number).After((int)_standardTimeoutUnit.TotalMilliseconds, 100));
            Assert.That(_blockTree.BestSuggestedHeader!.Hash, Is.EqualTo(miner1Tree.BestSuggestedHeader!.Hash), "client agrees with miner after split");
        }

        [Test]
        public void Can_sync_on_split_of_length_6()
        {
            BlockTree miner1Tree = Build.A.BlockTree(_genesisBlock).OfChainLength(6).TestObject;
            ISyncPeer miner1 = new SyncPeerMock(miner1Tree);

            SyncPeerPool.Start();
            Synchronizer.Start();
            SyncPeerPool.AddPeer(miner1);

            Assert.That(() => _blockTree.BestSuggestedHeader!.Hash, Is.EqualTo(miner1Tree.BestSuggestedHeader!.Hash).After((int)_standardTimeoutUnit.TotalMilliseconds, 100), "client agrees with miner before split");

            miner1Tree.AddBranch(7, 0, 1);

            Assert.That(() => _blockTree.BestSuggestedHeader!.Hash, Is.Not.EqualTo(miner1Tree.BestSuggestedHeader.Hash).After((int)_standardTimeoutUnit.TotalMilliseconds, 100), "client does not agree with miner after split");

            SyncServer.AddNewBlock(miner1Tree.RetrieveHeadBlock()!, miner1);

            Assert.That(() => _blockTree.BestSuggestedHeader!.Hash, Is.EqualTo(miner1Tree.BestSuggestedHeader.Hash).After((int)_standardTimeoutUnit.TotalMilliseconds, 100), "client agrees with miner after split");
        }

        [Test]
        [Ignore("Review sync manager tests")]
        public async Task Does_not_do_full_sync_when_not_needed()
        {
            BlockTree minerTree = Build.A.BlockTree(_genesisBlock).OfChainLength(6).TestObject;
            ISyncPeer miner1 = new SyncPeerMock(minerTree);

            AutoResetEvent resetEvent = new(false);
            Synchronizer.SyncEvent += (_, args) =>
            {
                if (args.SyncEvent == SyncEvent.Completed || args.SyncEvent == SyncEvent.Failed) resetEvent.Set();
            };

            SyncPeerPool.Start();
            Synchronizer.Start();
            SyncPeerPool.AddPeer(miner1);
            resetEvent.WaitOne(_standardTimeoutUnit);

            Assert.That(_blockTree.BestSuggestedHeader!.Hash, Is.EqualTo(minerTree.BestSuggestedHeader!.Hash), "client agrees with miner before split");

            Block newBlock = Build.A.Block.WithParent(minerTree.Head!).TestObject;
            minerTree.SuggestBlock(newBlock);
            minerTree.UpdateMainChain(newBlock);

            ISyncPeer miner2 = Substitute.For<ISyncPeer>();
            miner2.GetHeadBlockHeader(Arg.Any<Hash256>(), Arg.Any<CancellationToken>()).Returns(miner1.GetHeadBlockHeader(null, CancellationToken.None));
            miner2.Node.Id.Returns(TestItem.PublicKeyB);

            Assert.That((await miner2.GetHeadBlockHeader(null, Arg.Any<CancellationToken>()))?.Number, Is.EqualTo(newBlock.Number), "number as expected");

            SyncPeerPool.Start();
            Synchronizer.Start();
            SyncPeerPool.AddPeer(miner2);
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
            Synchronizer.SyncEvent += (_, args) =>
            {
                if (args.SyncEvent == SyncEvent.Completed || args.SyncEvent == SyncEvent.Failed) resetEvent.Set();
            };

            SyncPeerPool.Start();
            Synchronizer.Start();
            SyncPeerPool.AddPeer(miner1);
            resetEvent.WaitOne(_standardTimeoutUnit);

            Assert.That(_blockTree.BestSuggestedHeader!.Hash, Is.EqualTo(minerTree.BestSuggestedHeader!.Hash), "client agrees with miner before split");

            Block newBlock = Build.A.Block.WithParent(minerTree.Head!).TestObject;
            minerTree.SuggestBlock(newBlock);
            minerTree.UpdateMainChain(newBlock);

            ISyncPeer miner2 = Substitute.For<ISyncPeer>();
            miner2.GetHeadBlockHeader(Arg.Any<Hash256>(), Arg.Any<CancellationToken>()).Returns(miner1.GetHeadBlockHeader(null, CancellationToken.None));
            miner2.Node.Id.Returns(TestItem.PublicKeyB);

            Assert.That((await miner2.GetHeadBlockHeader(null, Arg.Any<CancellationToken>()))?.Number, Is.EqualTo(newBlock.Number), "number as expected");

            SyncPeerPool.Start();
            Synchronizer.Start();
            SyncPeerPool.AddPeer(miner2);
            resetEvent.WaitOne(_standardTimeoutUnit);

            await miner2.Received().GetBlockHeaders(6, 1, 0, default);
        }

        [Test]
        public void Can_retrieve_node_values()
        {
            _stateDb.Set(TestItem.KeccakA, TestItem.RandomDataA);
            IOwnedReadOnlyList<byte[]?> data = SyncServer.GetNodeData(new[] { TestItem.KeccakA, TestItem.KeccakB }, CancellationToken.None);

            Assert.That(data, Is.Not.Null);
            Assert.That(data.Count, Is.EqualTo(2), "data.Length");
            Assert.That(data[0], Is.EqualTo(TestItem.RandomDataA), "data[0]");
            Assert.That(data[1], Is.EqualTo(null), "data[1]");
        }

        [Test]
        public void Can_retrieve_empty_receipts()
        {
            _blockTree = Build.A.BlockTree(_genesisBlock).OfChainLength(2).TestObject;
            Block? block0 = _blockTree.FindBlock(0, BlockTreeLookupOptions.None);
            Block? block1 = _blockTree.FindBlock(1, BlockTreeLookupOptions.None);

            SyncServer.GetReceipts(block0!.Hash!).Should().HaveCount(0);
            SyncServer.GetReceipts(block1!.Hash!).Should().HaveCount(0);
            SyncServer.GetReceipts(TestItem.KeccakA).Should().HaveCount(0);
        }
    }
}
