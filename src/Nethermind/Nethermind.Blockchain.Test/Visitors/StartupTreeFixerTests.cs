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
// 

using System.Threading;
using FluentAssertions;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Blockchain.Visitors;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.State.Repositories;
using Nethermind.Db.Blooms;
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
            BlockTree tree = new BlockTree(blocksDb, headersDb, blockInfosDb, new ChainLevelInfoRepository(blockInfosDb), MainnetSpecProvider.Instance, NullTxPool.Instance, NullBloomStorage.Instance, LimboLogs.Instance);
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

            tree = new BlockTree(blocksDb, headersDb, blockInfosDb, new ChainLevelInfoRepository(blockInfosDb), MainnetSpecProvider.Instance, NullTxPool.Instance, NullBloomStorage.Instance, LimboLogs.Instance);
            
            StartupBlockTreeFixer fixer = new StartupBlockTreeFixer(new SyncConfig(), tree, LimboNoErrorLogger.Instance);
            tree.Accept(fixer, CancellationToken.None);

            Assert.Null(blockInfosDb.Get(3), "level 3");
            Assert.Null(blockInfosDb.Get(4), "level 4");
            Assert.Null(blockInfosDb.Get(5), "level 5");

            tree.Head.Header.Should().BeEquivalentTo(block2.Header, options => options.Excluding(t => t.MaybeParent));
            tree.BestSuggestedHeader.Should().BeEquivalentTo(block2.Header, options => options.Excluding(t => t.MaybeParent));
            tree.BestSuggestedBody.Should().BeEquivalentTo(block2.Body);
            tree.BestKnownNumber.Should().Be(2);
        }
        
        [Ignore("It is causing some trouble now. Disabling it while the restarts logic is under review")]
        [Test]
        public void When_head_block_is_followed_by_a_block_bodies_gap_it_should_delete_all_levels_after_the_gap_start()
        {
            MemDb blocksDb = new MemDb();
            MemDb blockInfosDb = new MemDb();
            MemDb headersDb = new MemDb();
            BlockTree tree = new BlockTree(blocksDb, headersDb, blockInfosDb, new ChainLevelInfoRepository(blockInfosDb), MainnetSpecProvider.Instance, NullTxPool.Instance, NullBloomStorage.Instance, LimboLogs.Instance);
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

            StartupBlockTreeFixer fixer = new StartupBlockTreeFixer(new SyncConfig(), tree, LimboNoErrorLogger.Instance);
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