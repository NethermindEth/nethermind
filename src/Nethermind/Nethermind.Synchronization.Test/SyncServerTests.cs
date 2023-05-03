// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Consensus;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Merge.Plugin;
using Nethermind.Merge.Plugin.Handlers;
using Nethermind.Merge.Plugin.InvalidChainTracker;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.State;
using Nethermind.Stats.Model;
using Nethermind.Synchronization.FastSync;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;
using NSubstitute;
using NUnit.Framework;
using BlockTree = Nethermind.Blockchain.BlockTree;

namespace Nethermind.Synchronization.Test
{
    [Parallelizable(ParallelScope.All)]
    [TestFixture]
    public class SyncServerTests
    {
        [Test]
        public void When_finding_hash_it_does_not_load_headers()
        {
            Context ctx = new();
            ctx.BlockTree.FindHash(123).Returns(TestItem.KeccakA);
            Keccak result = ctx.SyncServer.FindHash(123);

            ctx.BlockTree.DidNotReceive().FindHeader(Arg.Any<long>(), Arg.Any<BlockTreeLookupOptions>());
            ctx.BlockTree.DidNotReceive().FindHeader(Arg.Any<Keccak>(), Arg.Any<BlockTreeLookupOptions>());
            ctx.BlockTree.DidNotReceive().FindBlock(Arg.Any<Keccak>(), Arg.Any<BlockTreeLookupOptions>());
            Assert.That(result, Is.EqualTo(TestItem.KeccakA));
        }

        [Test]
        public void Does_not_request_peer_refresh_on_known_hints()
        {
            Context ctx = new();
            ctx.BlockTree.IsKnownBlock(1, TestItem.KeccakA).ReturnsForAnyArgs(true);
            ctx.SyncServer.HintBlock(TestItem.KeccakA, 1, ctx.NodeWhoSentTheBlock);
            ctx.PeerPool.DidNotReceiveWithAnyArgs().RefreshTotalDifficulty(null!, null!);
        }

        [Test]
        public void Requests_peer_refresh_on_unknown_hints()
        {
            Context ctx = new();
            ctx.BlockTree.IsKnownBlock(1, TestItem.KeccakA).ReturnsForAnyArgs(false);
            ctx.SyncServer.HintBlock(TestItem.KeccakA, 1, ctx.NodeWhoSentTheBlock);
            ctx.PeerPool.Received().ReceivedWithAnyArgs();
        }

        [Test]
        public void When_finding_by_hash_block_info_is_not_loaded()
        {
            Context ctx = new();
            ctx.SyncServer.Find(TestItem.KeccakA);
            ctx.BlockTree.Received().FindBlock(Arg.Any<Keccak>(), BlockTreeLookupOptions.TotalDifficultyNotNeeded);
        }

        [TestCase(true, true, true)]
        [TestCase(false, true, false)]
        [TestCase(true, false, false)]
        public void Can_accept_new_valid_blocks(bool sealOk, bool validationOk, bool accepted)
        {
            Context ctx = new();
            BlockTree remoteBlockTree = Build.A.BlockTree().OfChainLength(10).TestObject;
            BlockTree localBlockTree = Build.A.BlockTree().OfChainLength(9).TestObject;

            ISealValidator sealValidator = sealOk ? Always.Valid : Always.Invalid;
            IBlockValidator blockValidator = validationOk ? Always.Valid : Always.Invalid;
            ctx.SyncServer = new SyncServer(
                new MemDb(),
                new MemDb(),
                localBlockTree,
                NullReceiptStorage.Instance,
                blockValidator,
                sealValidator,
                ctx.PeerPool,
                StaticSelector.Full,
                new SyncConfig(),
                NullWitnessCollector.Instance,
                Policy.FullGossip,
                MainnetSpecProvider.Instance,
                LimboLogs.Instance);

            Block block = remoteBlockTree.FindBlock(9, BlockTreeLookupOptions.None);

            if (!accepted)
            {
                Assert.Throws<EthSyncException>(() => ctx.SyncServer.AddNewBlock(block, ctx.NodeWhoSentTheBlock));
            }
            else
            {
                ctx.SyncServer.AddNewBlock(block, ctx.NodeWhoSentTheBlock);
            }

            if (accepted)
            {
                Assert.That(block.Header, Is.EqualTo(localBlockTree.BestSuggestedHeader));
            }
            else
            {
                Assert.That(block.Header, Is.Not.EqualTo(localBlockTree.BestSuggestedHeader));
            }
        }

        [Test]
        public void Can_accept_blocks_that_are_fine()
        {
            Context ctx = new();
            BlockTree remoteBlockTree = Build.A.BlockTree().OfChainLength(10).TestObject;
            BlockTree localBlockTree = Build.A.BlockTree().OfChainLength(9).TestObject;

            ctx.SyncServer = new SyncServer(
                new MemDb(),
                new MemDb(),
                localBlockTree,
                NullReceiptStorage.Instance,
                Always.Valid,
                Always.Valid,
                ctx.PeerPool,
                StaticSelector.Full,
                new SyncConfig(),
                NullWitnessCollector.Instance,
                Policy.FullGossip,
                MainnetSpecProvider.Instance,
                LimboLogs.Instance);

            Block block = remoteBlockTree.FindBlock(9, BlockTreeLookupOptions.None);

            ctx.SyncServer.AddNewBlock(block, ctx.NodeWhoSentTheBlock);

            Assert.That(block.Header, Is.EqualTo(localBlockTree.BestSuggestedHeader));
        }

        [Test]
        public void Terminal_block_with_lower_td_should_not_change_best_suggested_but_should_be_added_to_block_tree()
        {
            Context ctx = new();
            BlockTree remoteBlockTree = Build.A.BlockTree().OfChainLength(10).TestObject;
            BlockTree localBlockTree = Build.A.BlockTree().OfChainLength(9).TestObject;
            TestSpecProvider testSpecProvider = new(London.Instance);
            testSpecProvider.TerminalTotalDifficulty = 10_000_000;

            Block newBestLocalBlock = Build.A.Block.WithNumber(localBlockTree.Head!.Number + 1).WithParent(localBlockTree.Head!).WithDifficulty(10_000_002L).TestObject;
            localBlockTree.SuggestBlock(newBestLocalBlock);

            PoSSwitcher poSSwitcher = new(new MergeConfig() { TerminalTotalDifficulty = $"{testSpecProvider.TerminalTotalDifficulty}" }, new SyncConfig(), new MemDb(), localBlockTree, testSpecProvider, LimboLogs.Instance);
            HeaderValidator headerValidator = new(
                localBlockTree,
                Always.Valid,
                testSpecProvider,
                LimboLogs.Instance);

            MergeHeaderValidator mergeHeaderValidator = new(poSSwitcher, headerValidator, localBlockTree, testSpecProvider, Always.Valid, LimboLogs.Instance);
            BlockValidator blockValidator = new(
                Always.Valid,
                mergeHeaderValidator,
                Always.Valid,
                MainnetSpecProvider.Instance,
                LimboLogs.Instance);

            ctx.SyncServer = new SyncServer(
                new MemDb(),
                new MemDb(),
                localBlockTree,
                NullReceiptStorage.Instance,
                blockValidator,
                Always.Valid,
                ctx.PeerPool,
                StaticSelector.Full,
                new SyncConfig(),
                NullWitnessCollector.Instance,
                Policy.FullGossip,
                testSpecProvider,
                LimboLogs.Instance);

            Block remoteBestBlock = remoteBlockTree.FindBlock(9, BlockTreeLookupOptions.None);

            ctx.SyncServer.AddNewBlock(remoteBestBlock, ctx.NodeWhoSentTheBlock);
            Assert.That(localBlockTree.BestSuggestedHeader!.Hash, Is.EqualTo(newBestLocalBlock.Header.Hash));
            Assert.That(localBlockTree.FindBlock(remoteBestBlock.Hash, BlockTreeLookupOptions.None)!.Hash, Is.EqualTo(remoteBestBlock.Hash));
        }

        [TestCase(10000000)]
        [TestCase(20000000)]
        public void Fake_total_difficulty_from_peer_does_not_trick_the_node(long ttd)
        {
            BlockTree remoteBlockTree = Build.A.BlockTree().OfChainLength(10).TestObject;
            Context ctx = CreateMergeContext(9, (UInt256)ttd);

            Block block = remoteBlockTree.FindBlock(9, BlockTreeLookupOptions.None);
            block.Header.TotalDifficulty *= 2;

            ctx.SyncServer.AddNewBlock(block, ctx.NodeWhoSentTheBlock);
            Assert.That(block.Header.Hash, Is.EqualTo(ctx.LocalBlockTree.BestSuggestedHeader!.Hash));

            Block parentBlock = remoteBlockTree.FindBlock(8, BlockTreeLookupOptions.None);
            Assert.That(ctx.LocalBlockTree.BestSuggestedHeader.TotalDifficulty, Is.EqualTo(parentBlock.TotalDifficulty + block.Difficulty));
        }

        [TestCase(9000000, true)]
        [TestCase(9000000, false)]
        [TestCase(8000010, true)]
        public void Can_reject_block_POW_block_after_TTD(long ttd, bool sendFakeTd)
        {
            BlockTree remoteBlockTree = Build.A.BlockTree().OfChainLength(10).TestObject;
            Context ctx = CreateMergeContext(9, (UInt256)ttd);

            Block block = remoteBlockTree.FindBlock(9, BlockTreeLookupOptions.None);
            if (sendFakeTd)
            {
                block.Header.TotalDifficulty *= 2;
            }

            Assert.Throws<EthSyncException>(() => ctx.SyncServer.AddNewBlock(block, ctx.NodeWhoSentTheBlock));
            Assert.That(ctx.LocalBlockTree.BestSuggestedHeader!.Number, Is.EqualTo(8));
        }

        [TestCase(9000000, true)]
        [TestCase(9000000, false)]
        [TestCase(8000010, true)]
        public void Post_merge_blocks_wont_be_accepted_by_gossip(long ttd, bool sendFakeTd)
        {
            BlockTree remoteBlockTree = Build.A.BlockTree().OfChainLength(9).TestObject;
            Block postMergeBlock = Build.A.Block.WithDifficulty(0).WithParent(remoteBlockTree.Head).WithTotalDifficulty(remoteBlockTree.Head.TotalDifficulty).WithNonce(0u).TestObject;
            remoteBlockTree.SuggestBlock(postMergeBlock);
            Context ctx = CreateMergeContext(9, (UInt256)ttd);

            Block block = remoteBlockTree.FindBlock(9, BlockTreeLookupOptions.None);
            if (sendFakeTd)
            {
                block.Header.TotalDifficulty *= 2;
            }

            ctx.SyncServer.AddNewBlock(block, ctx.NodeWhoSentTheBlock);
            Assert.That(ctx.LocalBlockTree.BestSuggestedHeader!.Number, Is.EqualTo(8));
            ctx.LocalBlockTree.FindBlock(postMergeBlock.Hash!, BlockTreeLookupOptions.None).Should().BeNull();
        }

        [TestCase(9000010, true, 100)]
        [TestCase(9000010, false, 100)]
        [TestCase(9000010, false, 1000000)]
        [TestCase(9000010, true, 1000000)]
        public void Can_inject_terminal_block_with_not_higher_td_than_head(long ttd, bool sendFakeTd, long difficulty)
        {
            BlockTree remoteBlockTree = Build.A.BlockTree().OfChainLength(9).TestObject;
            Block terminalBlockWithLowerDifficulty = Build.A.Block.WithDifficulty((UInt256)difficulty).WithParent(remoteBlockTree.Head).WithGasLimit(remoteBlockTree.Head.GasLimit + 1).WithTotalDifficulty(remoteBlockTree.Head.TotalDifficulty + (UInt256)difficulty).TestObject;
            remoteBlockTree.SuggestBlock(terminalBlockWithLowerDifficulty);
            Context ctx = CreateMergeContext(10, (UInt256)ttd);
            Assert.True(terminalBlockWithLowerDifficulty.IsTerminalBlock(ctx.SpecProvider));

            Block block = remoteBlockTree.FindBlock(9, BlockTreeLookupOptions.None);
            if (sendFakeTd)
            {
                block.Header.TotalDifficulty *= 2;
            }

            ctx.SyncServer.AddNewBlock(block, ctx.NodeWhoSentTheBlock);
            Assert.That(ctx.LocalBlockTree.BestSuggestedHeader!.Number, Is.EqualTo(9));
            ctx.LocalBlockTree.FindBlock(terminalBlockWithLowerDifficulty.Hash!, BlockTreeLookupOptions.None).Should().NotBeNull();
            ctx.LocalBlockTree.BestSuggestedHeader!.Hash.Should().NotBe(terminalBlockWithLowerDifficulty.Hash!);
        }

        [TestCase(9000010, true)]
        [TestCase(9000010, false)]
        public void Can_inject_terminal_block_with_higher_td_than_head(long ttd, bool sendFakeTd)
        {
            BlockTree remoteBlockTree = Build.A.BlockTree().OfChainLength(9).TestObject;
            Block terminalBlockWithHigherTotalDifficulty = Build.A.Block.WithDifficulty(1000010).WithParent(remoteBlockTree.Head).WithTotalDifficulty(remoteBlockTree.Head.TotalDifficulty + 1000010).TestObject;
            remoteBlockTree.SuggestBlock(terminalBlockWithHigherTotalDifficulty);
            Context ctx = CreateMergeContext(10, (UInt256)ttd);
            Assert.True(terminalBlockWithHigherTotalDifficulty.IsTerminalBlock(ctx.SpecProvider));

            Block block = remoteBlockTree.FindBlock(9, BlockTreeLookupOptions.None);
            if (sendFakeTd)
            {
                block.Header.TotalDifficulty *= 2;
            }

            ctx.SyncServer.AddNewBlock(block, ctx.NodeWhoSentTheBlock);
            Assert.That(ctx.LocalBlockTree.BestSuggestedHeader!.Number, Is.EqualTo(9));
            ctx.LocalBlockTree.FindBlock(terminalBlockWithHigherTotalDifficulty.Hash!, BlockTreeLookupOptions.None).Should().NotBeNull();
            ctx.LocalBlockTree.BestSuggestedHeader!.Hash.Should().Be(terminalBlockWithHigherTotalDifficulty.Hash!);
        }


        [TestCase(9000010)]
        public void PostMerge_block_from_gossip_should_not_override_main_chain(long ttd)
        {
            BlockTree remoteBlockTree = Build.A.BlockTree().OfChainLength(9).TestObject;
            Block poWBlockPostMerge = Build.A.Block.WithDifficulty(1000010).WithParent(remoteBlockTree.Head).WithTotalDifficulty(remoteBlockTree.Head.TotalDifficulty + 1000010).TestObject;
            remoteBlockTree.SuggestBlock(poWBlockPostMerge);

            Context ctx = CreateMergeContext(10, (UInt256)ttd);
            Block newPostMergeBlock = Build.A.Block.WithDifficulty(0).WithParent(ctx.LocalBlockTree.Head).WithTotalDifficulty(ctx.LocalBlockTree.Head.TotalDifficulty).TestObject;
            ctx.LocalBlockTree.SuggestBlock(newPostMergeBlock);
            ctx.LocalBlockTree.UpdateMainChain(new[] { newPostMergeBlock }, true, true);

            Block block = remoteBlockTree.FindBlock(9, BlockTreeLookupOptions.None);

            ctx.SyncServer.AddNewBlock(block, ctx.NodeWhoSentTheBlock);
            Assert.That(ctx.LocalBlockTree.BestSuggestedHeader!.Number, Is.EqualTo(10));
            ctx.LocalBlockTree.FindBlock(poWBlockPostMerge.Hash!, BlockTreeLookupOptions.None).Should().NotBeNull();
            ctx.LocalBlockTree.BestSuggestedHeader!.Hash.Should().Be(newPostMergeBlock.Hash!);
            ctx.LocalBlockTree.FindCanonicalBlockInfo(poWBlockPostMerge.Number).BlockHash.Should().NotBe(poWBlockPostMerge.Hash);
        }


        private Context CreateMergeContext(int blockTreeChainLength, UInt256 ttd)
        {
            Context ctx = new();
            TestSpecProvider testSpecProvider = new(London.Instance);
            testSpecProvider.TerminalTotalDifficulty = ttd;
            Block genesis = Build.A.Block.Genesis.TestObject;
            BlockTree localBlockTree = Build.A.BlockTree(genesis, testSpecProvider).OfChainLength(blockTreeChainLength).TestObject;

            PoSSwitcher poSSwitcher = new(new MergeConfig() { TerminalTotalDifficulty = $"{ttd}" }, new SyncConfig(), new MemDb(), localBlockTree, testSpecProvider, LimboLogs.Instance);
            MergeSealEngine sealEngine = new(new SealEngine(new NethDevSealEngine(), Always.Valid), poSSwitcher, new MergeSealValidator(poSSwitcher, Always.Valid), LimboLogs.Instance);
            HeaderValidator headerValidator = new(
                localBlockTree,
                sealEngine,
                testSpecProvider,
                LimboLogs.Instance);
            MergeHeaderValidator mergeHeaderValidator = new(poSSwitcher, headerValidator, localBlockTree, testSpecProvider, Always.Valid, LimboLogs.Instance);

            InvalidChainTracker invalidChainTracker = new(
                poSSwitcher,
                localBlockTree,
                new BlockCacheService(),
                LimboLogs.Instance);
            InvalidHeaderInterceptor headerValidatorWithInterceptor = new(
                mergeHeaderValidator,
                invalidChainTracker,
                LimboLogs.Instance);
            BlockValidator blockValidator = new(
                Always.Valid,
                headerValidatorWithInterceptor,
                Always.Valid,
                MainnetSpecProvider.Instance,
                LimboLogs.Instance);

            ctx.SyncServer = new SyncServer(
                new MemDb(),
                new MemDb(),
                localBlockTree,
                NullReceiptStorage.Instance,
                blockValidator,
                sealEngine,
                ctx.PeerPool,
                StaticSelector.Full,
                new SyncConfig(),
                NullWitnessCollector.Instance,
                Policy.FullGossip,
                testSpecProvider,
                LimboLogs.Instance);
            ctx.SpecProvider = testSpecProvider;
            ctx.LocalBlockTree = localBlockTree;

            return ctx;
        }


        [Test]
        public void Will_not_reject_block_with_bad_total_diff_but_will_reset_diff_to_null()
        {
            Context ctx = new();
            BlockTree remoteBlockTree = Build.A.BlockTree().OfChainLength(10).TestObject;
            BlockTree localBlockTree = Build.A.BlockTree().OfChainLength(9).TestObject;

            HeaderValidator headerValidator = new(
                localBlockTree,
                Always.Valid,
                MainnetSpecProvider.Instance,
                LimboLogs.Instance);

            BlockValidator blockValidator = new(
                Always.Valid,
                headerValidator,
                Always.Valid,
                MainnetSpecProvider.Instance,
                LimboLogs.Instance);

            ctx.SyncServer = new SyncServer(
                new MemDb(),
                new MemDb(),
                localBlockTree,
                NullReceiptStorage.Instance,
                blockValidator,
                Always.Valid,
                ctx.PeerPool,
                StaticSelector.Full,
                new SyncConfig(),
                NullWitnessCollector.Instance,
                Policy.FullGossip,
                MainnetSpecProvider.Instance,
                LimboLogs.Instance);

            Block block = remoteBlockTree.FindBlock(9, BlockTreeLookupOptions.None);
            block.Header.TotalDifficulty *= 2;

            ctx.SyncServer.AddNewBlock(block, ctx.NodeWhoSentTheBlock);
            Assert.That(block.Header.Hash, Is.EqualTo(localBlockTree.BestSuggestedHeader!.Hash));

            Block parentBlock = remoteBlockTree.FindBlock(8, BlockTreeLookupOptions.None);
            Assert.That(localBlockTree.BestSuggestedHeader.TotalDifficulty, Is.EqualTo(parentBlock.TotalDifficulty + block.Difficulty));
        }

        [Test]
        public void Rejects_new_old_blocks()
        {
            Context ctx = new();
            BlockTree remoteBlockTree = Build.A.BlockTree().OfChainLength(10).TestObject;
            BlockTree localBlockTree = Build.A.BlockTree().OfChainLength(600).TestObject;

            ISealValidator sealValidator = Substitute.For<ISealValidator>();
            ctx.SyncServer = new SyncServer(
                new MemDb(),
                new MemDb(),
                localBlockTree,
                NullReceiptStorage.Instance,
                Always.Valid,
                sealValidator,
                ctx.PeerPool,
                StaticSelector.Full,
                new SyncConfig(),
                NullWitnessCollector.Instance,
                Policy.FullGossip,
                MainnetSpecProvider.Instance,
                LimboLogs.Instance);

            Block block = remoteBlockTree.FindBlock(9, BlockTreeLookupOptions.None);

            ctx.SyncServer.AddNewBlock(block, ctx.NodeWhoSentTheBlock);

            sealValidator.DidNotReceive().ValidateSeal(Arg.Any<BlockHeader>(), Arg.Any<bool>());
        }

        [Test]
        public async Task Broadcast_NewBlock_on_arrival()
        {
            Context ctx = new();
            BlockTree remoteBlockTree = Build.A.BlockTree().OfChainLength(10).TestObject;
            BlockTree localBlockTree = Build.A.BlockTree().OfChainLength(9).TestObject;
            ctx.SyncServer = new SyncServer(
                new MemDb(),
                new MemDb(),
                localBlockTree,
                NullReceiptStorage.Instance,
                Always.Valid,
                Always.Valid,
                ctx.PeerPool,
                StaticSelector.Full,
                new SyncConfig(),
                NullWitnessCollector.Instance,
                Policy.FullGossip,
                MainnetSpecProvider.Instance,
                LimboLogs.Instance);

            ISyncServer remoteServer1 = Substitute.For<ISyncServer>();
            SyncPeerMock syncPeerMock1 = new(remoteBlockTree, TestItem.PublicKeyA, remoteSyncServer: remoteServer1);
            PeerInfo peer1 = new(syncPeerMock1);
            ISyncServer remoteServer2 = Substitute.For<ISyncServer>();
            SyncPeerMock syncPeerMock2 = new(remoteBlockTree, TestItem.PublicKeyB, remoteSyncServer: remoteServer2);
            PeerInfo peer2 = new(syncPeerMock2);
            PeerInfo[] peers = { peer1, peer2 };
            ctx.PeerPool.AllPeers.Returns(peers);
            ctx.PeerPool.PeerCount.Returns(peers.Length);
            ctx.SyncServer.AddNewBlock(remoteBlockTree.Head!, peer1.SyncPeer);
            ctx.SyncServer.AddNewBlock(remoteBlockTree.Head!, peer2.SyncPeer);
            await Task.Delay(100); // notifications fire on separate task
            await Task.WhenAll(syncPeerMock1.Close(), syncPeerMock2.Close());
            remoteServer1.DidNotReceive().AddNewBlock(remoteBlockTree.Head!, Arg.Any<ISyncPeer>());
            remoteServer2.Received().AddNewBlock(Arg.Is<Block>(b => b.Hash == remoteBlockTree.Head!.Hash), Arg.Any<ISyncPeer>());
        }

        [Test]
        public async Task Skip_known_block()
        {
            Context ctx = new();
            BlockTree blockTree = Build.A.BlockTree().OfChainLength(9).TestObject;
            ctx.SyncServer = new SyncServer(
                new MemDb(),
                new MemDb(),
                blockTree,
                NullReceiptStorage.Instance,
                Always.Valid,
                Always.Valid,
                ctx.PeerPool,
                StaticSelector.Full,
                new SyncConfig(),
                NullWitnessCollector.Instance,
                Policy.FullGossip,
                MainnetSpecProvider.Instance,
                LimboLogs.Instance);

            ISyncServer remoteServer1 = Substitute.For<ISyncServer>();
            SyncPeerMock syncPeerMock1 = new(blockTree, TestItem.PublicKeyA, remoteSyncServer: remoteServer1);
            PeerInfo peer1 = new(syncPeerMock1);
            ISyncServer remoteServer2 = Substitute.For<ISyncServer>();
            SyncPeerMock syncPeerMock2 = new(blockTree, TestItem.PublicKeyB, remoteSyncServer: remoteServer2);
            PeerInfo peer2 = new(syncPeerMock2);
            PeerInfo[] peers = { peer1, peer2 };
            ctx.PeerPool.AllPeers.Returns(peers);
            ctx.PeerPool.PeerCount.Returns(peers.Length);
            Block head = blockTree.Head!;
            ctx.SyncServer.AddNewBlock(head, peer1.SyncPeer);
            await Task.Delay(100); // notifications fire on separate task
            await Task.WhenAll(syncPeerMock1.Close(), syncPeerMock2.Close());
            remoteServer1.DidNotReceive().AddNewBlock(head, Arg.Any<ISyncPeer>());
            remoteServer2.DidNotReceive().AddNewBlock(head, Arg.Any<ISyncPeer>());
            blockTree.FindLevel(head.Number)!.BlockInfos.Length.Should().Be(1);
        }

        [Test]
        [Retry(3)]
        public async Task Broadcast_NewBlock_on_arrival_to_sqrt_of_peers([Values(1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 20, 50, 100)] int peerCount)
        {
            int expectedPeers = (int)Math.Ceiling(Math.Sqrt(peerCount - 1)); // -1 because of ignoring sender

            Context ctx = new();
            BlockTree remoteBlockTree = Build.A.BlockTree().OfChainLength(10).TestObject;
            BlockTree localBlockTree = Build.A.BlockTree().OfChainLength(9).TestObject;
            ctx.SyncServer = new SyncServer(
                new MemDb(),
                new MemDb(),
                localBlockTree,
                NullReceiptStorage.Instance,
                Always.Valid,
                Always.Valid,
                ctx.PeerPool,
                StaticSelector.Full,
                new SyncConfig(),
                NullWitnessCollector.Instance,
                Policy.FullGossip,
                MainnetSpecProvider.Instance,
                LimboLogs.Instance);

            ISyncServer remoteServer = Substitute.For<ISyncServer>();
            int count = 0;
            remoteServer
                .When(r => r.AddNewBlock(Arg.Is<Block>(b => b.Hash == remoteBlockTree.Head!.Hash), Arg.Any<ISyncPeer>()))
                .Do(_ => count++);
            PeerInfo[] peers = Enumerable.Range(0, peerCount).Take(peerCount)
                .Select(k => new PeerInfo(new SyncPeerMock(remoteBlockTree, remoteSyncServer: remoteServer)))
                .ToArray();
            ctx.PeerPool.AllPeers.Returns(peers);
            ctx.PeerPool.PeerCount.Returns(peers.Length);
            ctx.SyncServer.AddNewBlock(remoteBlockTree.Head!, peers[0].SyncPeer);

            Assert.That(() => count, Is.EqualTo(expectedPeers).After(5000, 100));
            await Task.WhenAll(peers.Select(p => ((SyncPeerMock)p.SyncPeer).Close()).ToArray());
        }

        [Test]
        public void GetNodeData_returns_cached_trie_nodes()
        {
            Context ctx = new();
            BlockTree localBlockTree = Build.A.BlockTree().OfChainLength(600).TestObject;
            ISealValidator sealValidator = Substitute.For<ISealValidator>();
            MemDb stateDb = new();
            TrieStore trieStore = new(stateDb, Prune.WhenCacheReaches(10.MB()), NoPersistence.Instance, LimboLogs.Instance);
            ctx.SyncServer = new SyncServer(
                trieStore,
                new MemDb(),
                localBlockTree,
                NullReceiptStorage.Instance,
                Always.Valid,
                sealValidator,
                ctx.PeerPool,
                StaticSelector.Full,
                new SyncConfig(),
                NullWitnessCollector.Instance,
                Policy.FullGossip,
                MainnetSpecProvider.Instance,
                LimboLogs.Instance);

            Keccak nodeKey = TestItem.KeccakA;
            TrieNode node = new(NodeType.Leaf, nodeKey, TestItem.KeccakB.Bytes);
            trieStore.CommitNode(1, new NodeCommitInfo(node));
            trieStore.FinishBlockCommit(TrieType.State, 1, node);

            stateDb.KeyExists(nodeKey).Should().BeFalse();
            ctx.SyncServer.GetNodeData(new[] { nodeKey }, NodeDataType.All).Should().BeEquivalentTo(new[] { TestItem.KeccakB.Bytes });
        }

        private class Context
        {
            public Context()
            {
                NodeWhoSentTheBlock = Substitute.For<ISyncPeer>();
                NodeWhoSentTheBlock.Node.Returns(new Node(TestItem.PublicKeyA, "127.0.0.1", 30303));
                PeerPool = Substitute.For<ISyncPeerPool>();

                BlockTree = Substitute.For<IBlockTree>();
                StaticSelector selector = StaticSelector.Full;
                SyncServer = new SyncServer(
                    new MemDb(),
                    new MemDb(),
                    BlockTree,
                    NullReceiptStorage.Instance,
                    Always.Valid,
                    Always.Valid,
                    PeerPool,
                    selector,
                    new SyncConfig(),
                    NullWitnessCollector.Instance,
                    Policy.FullGossip,
                    MainnetSpecProvider.Instance,
                    LimboLogs.Instance);
            }

            public IBlockTree BlockTree { get; }
            public ISyncPeerPool PeerPool { get; }
            public SyncServer SyncServer { get; set; }
            public ISpecProvider SpecProvider { get; set; }

            public IBlockTree LocalBlockTree { get; set; }
            public ISyncPeer NodeWhoSentTheBlock { get; }
        }
    }
}
