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

using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Blockchain.Validators;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.State;
using Nethermind.Stats.Model;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers;
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
        public void _When_finding_hash_it_does_not_load_headers()
        {
            Context ctx = new Context();
            ctx.BlockTree.FindHash(123).Returns(TestItem.KeccakA);
            Keccak result = ctx.SyncServer.FindHash(123);

            ctx.BlockTree.DidNotReceive().FindHeader(Arg.Any<long>(), Arg.Any<BlockTreeLookupOptions>());
            ctx.BlockTree.DidNotReceive().FindHeader(Arg.Any<Keccak>(), Arg.Any<BlockTreeLookupOptions>());
            ctx.BlockTree.DidNotReceive().FindBlock(Arg.Any<Keccak>(), Arg.Any<BlockTreeLookupOptions>());
            Assert.AreEqual(TestItem.KeccakA, result);
        }

        [Test]
        public void Does_not_request_peer_refresh_on_known_hints()
        {
            Context ctx = new Context();
            ctx.BlockTree.IsKnownBlock(1, TestItem.KeccakA).ReturnsForAnyArgs(true);
            ctx.SyncServer.HintBlock(TestItem.KeccakA, 1, ctx.NodeWhoSentTheBlock);
            ctx.PeerPool.DidNotReceiveWithAnyArgs().RefreshTotalDifficulty(null!, null!);
        }

        [Test]
        public void Requests_peer_refresh_on_unknown_hints()
        {
            Context ctx = new Context();
            ctx.BlockTree.IsKnownBlock(1, TestItem.KeccakA).ReturnsForAnyArgs(false);
            ctx.SyncServer.HintBlock(TestItem.KeccakA, 1, ctx.NodeWhoSentTheBlock);
            ctx.PeerPool.Received().ReceivedWithAnyArgs();
        }

        [Test]
        public void When_finding_by_hash_block_info_is_not_loaded()
        {
            Context ctx = new Context();
            ctx.SyncServer.Find(TestItem.KeccakA);
            ctx.BlockTree.Received().FindBlock(Arg.Any<Keccak>(), BlockTreeLookupOptions.TotalDifficultyNotNeeded);
        }

        [TestCase(true, true, true)]
        [TestCase(false, true, false)]
        [TestCase(true, false, false)]
        public void Can_accept_new_valid_blocks(bool sealOk, bool validationOk, bool accepted)
        {
            Context ctx = new Context();
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
                Assert.AreEqual(localBlockTree.BestSuggestedHeader, block.Header);
            }
            else
            {
                Assert.AreNotEqual(localBlockTree.BestSuggestedHeader, block.Header);
            }
        }

        [Test]
        public void Can_accept_blocks_that_are_fine()
        {
            Context ctx = new Context();
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
                LimboLogs.Instance);

            Block block = remoteBlockTree.FindBlock(9, BlockTreeLookupOptions.None);

            ctx.SyncServer.AddNewBlock(block, ctx.NodeWhoSentTheBlock);

            Assert.AreEqual(localBlockTree.BestSuggestedHeader, block.Header);
        }

        [Test]
        public void Will_not_reject_block_with_bad_total_diff_but_will_reset_diff_to_null()
        {
            Context ctx = new Context();
            BlockTree remoteBlockTree = Build.A.BlockTree().OfChainLength(10).TestObject;
            BlockTree localBlockTree = Build.A.BlockTree().OfChainLength(9).TestObject;

            HeaderValidator headerValidator = new HeaderValidator(
                localBlockTree,
                Always.Valid,
                MainnetSpecProvider.Instance,
                LimboLogs.Instance);

            BlockValidator blockValidator = new BlockValidator(
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
                LimboLogs.Instance);

            Block block = remoteBlockTree.FindBlock(9, BlockTreeLookupOptions.None);
            block.Header.TotalDifficulty *= 2;

            ctx.SyncServer.AddNewBlock(block, ctx.NodeWhoSentTheBlock);
            Assert.AreEqual(localBlockTree.BestSuggestedHeader.Hash, block.Header.Hash);

            Block parentBlock = remoteBlockTree.FindBlock(8, BlockTreeLookupOptions.None);
            Assert.AreEqual(parentBlock.TotalDifficulty + block.Difficulty, localBlockTree.BestSuggestedHeader.TotalDifficulty);
        }

        [Test]
        public void Rejects_new_old_blocks()
        {
            Context ctx = new Context();
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
                LimboLogs.Instance);

            Block block = remoteBlockTree.FindBlock(9, BlockTreeLookupOptions.None);

            ctx.SyncServer.AddNewBlock(block, ctx.NodeWhoSentTheBlock);

            sealValidator.DidNotReceive().ValidateSeal(Arg.Any<BlockHeader>(), Arg.Any<bool>());
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
                    LimboLogs.Instance);
            }

            public IBlockTree BlockTree { get; }
            public ISyncPeerPool PeerPool { get; }
            public SyncServer SyncServer { get; set; }
            public ISyncPeer NodeWhoSentTheBlock { get; }
        }
    }
}
