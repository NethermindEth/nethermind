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
// 

using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Blockchain.Processing;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Rewards;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Blockchain.Validators;
using Nethermind.Blockchain.Visitors;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.State.Repositories;
using Nethermind.Db.Blooms;
using Nethermind.Evm;
using Nethermind.JsonRpc.Test.Modules;
using Nethermind.Serialization.Rlp;
using Nethermind.State;
using Nethermind.Trie.Pruning;
using Nethermind.TxPool;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test.Visitors
{
    public class StartupTreeFixerTests
    {
        [Test, Ignore("Not implemented")]
        public void Cleans_missing_references_from_chain_level_info()
        {
            // for now let us just look at the warnings (before we start adding cleanup)
        }
        
        [Test, Ignore("Not implemented")]
        public void Warns_when_blocks_are_marked_as_processed_but_there_are_no_bodies()
        {
            // for now let us just look at the warnings (before we start adding cleanup)
        }
        
        [Test, Ignore("Not implemented")]
        public void Warns_when_there_is_a_hole_in_processed_blocks()
        {
        }
        
        [Test]
        public void Deletes_everything_after_the_missing_level()
        {
            MemDb blocksDb = new MemDb();
            MemDb blockInfosDb = new MemDb();
            MemDb headersDb = new MemDb();
            BlockTree tree = new BlockTree(blocksDb, headersDb, blockInfosDb, new ChainLevelInfoRepository(blockInfosDb), MainnetSpecProvider.Instance, NullBloomStorage.Instance, LimboLogs.Instance);
            Block block0 = Build.A.Block.WithNumber(0).WithDifficulty(1).TestObject;
            Block block1 = Build.A.Block.WithNumber(1).WithDifficulty(2).WithParent(block0).TestObject;
            Block block2 = Build.A.Block.WithNumber(2).WithDifficulty(3).WithParent(block1).TestObject;
            Block block3 = Build.A.Block.WithNumber(3).WithDifficulty(4).WithParent(block2).TestObject;
            Block block4 = Build.A.Block.WithNumber(4).WithDifficulty(5).WithParent(block3).TestObject;
            Block block5 = Build.A.Block.WithNumber(5).WithDifficulty(6).WithParent(block4).TestObject;

            tree.SuggestBlock(block0);
            tree.SuggestBlock(block1);
            tree.SuggestBlock(block2);
            tree.SuggestBlock(block3);
            tree.SuggestBlock(block4);
            tree.SuggestHeader(block5.Header);

            tree.UpdateMainChain(block0);
            tree.UpdateMainChain(block1);
            tree.UpdateMainChain(block2);
            
            blockInfosDb.Delete(3);

            tree = new BlockTree(blocksDb, headersDb, blockInfosDb, new ChainLevelInfoRepository(blockInfosDb), MainnetSpecProvider.Instance, NullBloomStorage.Instance, LimboLogs.Instance);
            
            StartupBlockTreeFixer fixer = new StartupBlockTreeFixer(new SyncConfig(), tree, new MemDb(), LimboNoErrorLogger.Instance);
            tree.Accept(fixer, CancellationToken.None);

            Assert.Null(blockInfosDb.Get(3), "level 3");
            Assert.Null(blockInfosDb.Get(4), "level 4");
            Assert.Null(blockInfosDb.Get(5), "level 5");

            tree.Head.Header.Should().BeEquivalentTo(block2.Header, options => options.Excluding(t => t.MaybeParent));
            tree.BestSuggestedHeader.Should().BeEquivalentTo(block2.Header, options => options.Excluding(t => t.MaybeParent));
            tree.BestSuggestedBody.Should().BeEquivalentTo(block2.Body);
            tree.BestKnownNumber.Should().Be(2);
        }
        
        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        [TestCase(4)]
        [TestCase(5)]
        [TestCase(6)]
        [TestCase(65)]
        public async Task Suggesting_blocks_works_correctly_after_processor_restart(int suggestedBlocksAmount)
        {
            TestRpcBlockchain testRpc = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).Build();
            await testRpc.BlockchainProcessor.StopAsync();
            IBlockTree tree = testRpc.BlockTree;
            long startingBlockNumber = tree.Head!.Number;

            SuggestNumberOfBlocks(tree, suggestedBlocksAmount);
            
            // simulating restarts - we stopped the old blockchain processor and create the new one
            BlockchainProcessor newBlockchainProcessor = new BlockchainProcessor(tree, testRpc.BlockProcessor,
                testRpc.BlockPreprocessorStep, LimboLogs.Instance, BlockchainProcessor.Options.Default);
            newBlockchainProcessor.Start();
            testRpc.BlockchainProcessor = newBlockchainProcessor;
            
            // fixing after restart
            StartupBlockTreeFixer fixer = new StartupBlockTreeFixer(new SyncConfig(), tree, testRpc.DbProvider.StateDb, LimboNoErrorLogger.Instance, 5);
            await tree.Accept(fixer, CancellationToken.None);

            // waiting for N new heads
            for (int i = 0; i < suggestedBlocksAmount; ++i)
            {
                await testRpc.WaitForNewHead();
            }

            // add a new block at the end
            await testRpc.AddBlock();
            Assert.AreEqual(startingBlockNumber + suggestedBlocksAmount + 1, tree.Head!.Number);
        }
        
        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        [TestCase(6)]
        public async Task Fixer_should_not_suggest_block_without_state(int suggestedBlocksAmount)
        {
            TestRpcBlockchain testRpc = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).Build();
            await testRpc.BlockchainProcessor.StopAsync();
            IBlockTree tree = testRpc.BlockTree;

            SuggestNumberOfBlocks(tree, suggestedBlocksAmount);
            
            // simulating restarts - we stopped the old blockchain processor and create the new one
            BlockchainProcessor newBlockchainProcessor = new BlockchainProcessor(tree, testRpc.BlockProcessor,
                testRpc.BlockPreprocessorStep, LimboLogs.Instance, BlockchainProcessor.Options.Default);
            newBlockchainProcessor.Start();
            testRpc.BlockchainProcessor = newBlockchainProcessor;
            
            // we create a new empty db for stateDb so we shouldn't suggest new blocks
            MemDb stateDb = new MemDb();
            IBlockTreeVisitor fixer = new StartupBlockTreeFixer(new SyncConfig(), tree, stateDb, LimboNoErrorLogger.Instance, 5);
            BlockVisitOutcome result = await fixer.VisitBlock(tree.Head!, CancellationToken.None);
            
            Assert.AreEqual(BlockVisitOutcome.None, result);
        }
        
        [Test]
        public async Task Fixer_should_not_suggest_block_with_null_block()
        {
            TestRpcBlockchain testRpc = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).Build();
            await testRpc.BlockchainProcessor.StopAsync();
            IBlockTree tree = testRpc.BlockTree;

            SuggestNumberOfBlocks(tree, 1);
            
            // simulating restarts - we stopped the old blockchain processor and create the new one
            BlockchainProcessor newBlockchainProcessor = new BlockchainProcessor(tree, testRpc.BlockProcessor,
                testRpc.BlockPreprocessorStep, LimboLogs.Instance, BlockchainProcessor.Options.Default);
            newBlockchainProcessor.Start();
            testRpc.BlockchainProcessor = newBlockchainProcessor;
            
            IBlockTreeVisitor fixer = new StartupBlockTreeFixer(new SyncConfig(), tree, testRpc.DbProvider.StateDb, LimboNoErrorLogger.Instance, 5);
            BlockVisitOutcome result = await fixer.VisitBlock(null, CancellationToken.None);
            
            Assert.AreEqual(BlockVisitOutcome.None, result);
        }

        private static void SuggestNumberOfBlocks(IBlockTree blockTree, int blockAmount)
        {
            Block newParent = blockTree.Head;
            for (int i = 0; i < blockAmount; ++i)
            {
                Block newBlock = Build.A.Block
                    .WithNumber(newParent!.Number + 1)
                    .WithDifficulty(newParent.Difficulty + 1)
                    .WithParent(newParent)
                    .WithStateRoot(newParent.StateRoot!).TestObject;
                blockTree.SuggestBlock(newBlock);
                newParent = newBlock;
            }
        }
        
        [Ignore("It is causing some trouble now. Disabling it while the restarts logic is under review")]
        [Test]
        public void When_head_block_is_followed_by_a_block_bodies_gap_it_should_delete_all_levels_after_the_gap_start()
        {
            MemDb blocksDb = new MemDb();
            MemDb blockInfosDb = new MemDb();
            MemDb headersDb = new MemDb();
            BlockTree tree = new BlockTree(blocksDb, headersDb, blockInfosDb, new ChainLevelInfoRepository(blockInfosDb), MainnetSpecProvider.Instance, NullBloomStorage.Instance, LimboLogs.Instance);
            Block block0 = Build.A.Block.WithNumber(0).WithDifficulty(1).TestObject;
            Block block1 = Build.A.Block.WithNumber(1).WithDifficulty(2).WithParent(block0).TestObject;
            Block block2 = Build.A.Block.WithNumber(2).WithDifficulty(3).WithParent(block1).TestObject;
            Block block3 = Build.A.Block.WithNumber(3).WithDifficulty(4).WithParent(block2).TestObject;
            Block block4 = Build.A.Block.WithNumber(4).WithDifficulty(5).WithParent(block3).TestObject;
            Block block5 = Build.A.Block.WithNumber(5).WithDifficulty(6).WithParent(block4).TestObject;

            tree.SuggestBlock(block0);
            tree.SuggestBlock(block1);
            tree.SuggestBlock(block2);
            tree.SuggestHeader(block3.Header);
            tree.SuggestHeader(block4.Header);
            tree.SuggestBlock(block5);

            tree.UpdateMainChain(block2);

            StartupBlockTreeFixer fixer = new StartupBlockTreeFixer(new SyncConfig(), tree, new MemDb(), LimboNoErrorLogger.Instance);
            tree.Accept(fixer, CancellationToken.None);

            Assert.Null(blockInfosDb.Get(3), "level 3");
            Assert.Null(blockInfosDb.Get(4), "level 4");
            Assert.Null(blockInfosDb.Get(5), "level 5");

            Assert.AreEqual(2L, tree.BestKnownNumber, "best known");
            Assert.AreEqual(block2.Header, tree.Head?.Header, "head");
            Assert.AreEqual(block2.Hash, tree.BestSuggestedHeader.Hash, "suggested");
        }
    }
}
