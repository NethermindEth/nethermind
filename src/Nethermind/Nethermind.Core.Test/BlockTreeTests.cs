/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System;
using System.Numerics;
using Nethermind.Blockchain;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using NUnit.Framework;

namespace Nethermind.Core.Test
{
    [TestFixture]
    public class BlockTreeTests
    {
        [Test]
        public void Add_genesis_shall_notify()
        {
            bool hasNotified = false;
            BlockTree blockTree = new BlockTree(OlympicSpecProvider.Instance, NullLogger.Instance);
            blockTree.NewHeadBlock += (sender, args) => { hasNotified = true; };

            Block block = Build.A.Block.WithNumber(0).TestObject;
            var result = blockTree.SuggestBlock(block);
            blockTree.MarkAsProcessed(block.Hash);
            blockTree.MoveToMain(block.Hash);

            Assert.True(hasNotified, "notification");
            Assert.AreEqual(AddBlockResult.Added, result, "result");
        }

        [Test]
        public void Can_only_add_genesis_once()
        {
            BlockTree blockTree = new BlockTree(OlympicSpecProvider.Instance, NullLogger.Instance);
            Block blockA = Build.A.Block.WithNumber(0).TestObject;
            Block blockB = Build.A.Block.WithNumber(0).TestObject;
            blockTree.SuggestBlock(blockA);
            Assert.Throws<InvalidOperationException>(() => blockTree.SuggestBlock(blockB));
        }

        [Test]
        public void Shall_notify_on_new_head_block_after_genesis()
        {
            bool hasNotified = false;
            BlockTree blockTree = new BlockTree(OlympicSpecProvider.Instance, NullLogger.Instance);
            Block block0 = Build.A.Block.WithNumber(0).WithDifficulty(1).TestObject;
            Block block1 = Build.A.Block.WithNumber(1).WithDifficulty(2).WithParent(block0).TestObject;
            blockTree.SuggestBlock(block0);
            blockTree.NewHeadBlock += (sender, args) => { hasNotified = true; };
            var result = blockTree.SuggestBlock(block1);
            blockTree.MarkAsProcessed(block1.Hash);
            blockTree.MoveToMain(block1.Hash);

            Assert.True(hasNotified, "notification");
            Assert.AreEqual(AddBlockResult.Added, result, "result");
        }
        
        [Test]
        public void Shall_notify_on_new_suggested_block_after_genesis()
        {
            bool hasNotified = false;
            BlockTree blockTree = new BlockTree(OlympicSpecProvider.Instance, NullLogger.Instance);
            Block block0 = Build.A.Block.WithNumber(0).WithDifficulty(1).TestObject;
            Block block1 = Build.A.Block.WithNumber(1).WithDifficulty(2).WithParent(block0).TestObject;
            blockTree.SuggestBlock(block0);
            blockTree.NewBestSuggestedBlock += (sender, args) => { hasNotified = true; };
            var result = blockTree.SuggestBlock(block1);

            Assert.True(hasNotified, "notification");
            Assert.AreEqual(AddBlockResult.Added, result, "result");
        }

        [Test]
        public void Shall_not_notify_but_add_on_lower_difficulty()
        {
            bool hasNotifiedBest = false;
            bool hasNotifiedHead = false;
            BlockTree blockTree = new BlockTree(OlympicSpecProvider.Instance, NullLogger.Instance);
            Block block0 = Build.A.Block.WithNumber(0).WithDifficulty(1).TestObject;
            Block block1 = Build.A.Block.WithNumber(1).WithDifficulty(3).WithParent(block0).TestObject;
            Block block2 = Build.A.Block.WithNumber(1).WithDifficulty(2).WithParent(block0).TestObject;
            blockTree.SuggestBlock(block0);
            blockTree.SuggestBlock(block1);
            blockTree.NewHeadBlock += (sender, args) => { hasNotifiedHead = true; };
            blockTree.NewBestSuggestedBlock += (sender, args) => { hasNotifiedBest = true; };
            var result = blockTree.SuggestBlock(block2);

            Assert.False(hasNotifiedBest, "notification best");
            Assert.False(hasNotifiedHead, "notification head");
            Assert.AreEqual(AddBlockResult.Added, result, "result");
        }

        [Test]
        public void Shall_ignore_orphans()
        {
            BlockTree blockTree = new BlockTree(OlympicSpecProvider.Instance, NullLogger.Instance);
            Block block0 = Build.A.Block.WithNumber(0).WithDifficulty(1).TestObject;
            Block block2 = Build.A.Block.WithNumber(2).WithDifficulty(3).TestObject;
            blockTree.SuggestBlock(block0);
            var result = blockTree.SuggestBlock(block2);
            Assert.AreEqual(AddBlockResult.UnknownParent, result);
        }
        
        [Test]
        public void Shall_ignore_known()
        {
            BlockTree blockTree = new BlockTree(OlympicSpecProvider.Instance, NullLogger.Instance);
            Block block0 = Build.A.Block.WithNumber(0).WithDifficulty(1).TestObject;
            Block block1 = Build.A.Block.WithNumber(1).WithDifficulty(2).WithParent(block0).TestObject;
            blockTree.SuggestBlock(block0);
            blockTree.SuggestBlock(block1);
            var result = blockTree.SuggestBlock(block1);
            Assert.AreEqual(AddBlockResult.AlreadyKnown, result);
        }

        [Test]
        public void Add_and_find_branch()
        {
            BlockTree blockTree = new BlockTree(OlympicSpecProvider.Instance, NullLogger.Instance);
            Block block = Build.A.Block.TestObject;
            blockTree.SuggestBlock(block);
            Block found = blockTree.FindBlock(block.Hash, false);
            Assert.AreSame(block, found);
        }

        [Test]
        public void Add_on_branch_move_find()
        {
            BlockTree blockTree = new BlockTree(OlympicSpecProvider.Instance, NullLogger.Instance);
            Block block = Build.A.Block.TestObject;
            AddToMain(blockTree, block);
            Block found = blockTree.FindBlock(block.Hash, true);
            Assert.AreSame(block, found);
        }

        [Test]
        public void Add_on_main_move_find()
        {
            BlockTree blockTree = new BlockTree(OlympicSpecProvider.Instance, NullLogger.Instance);
            Block block = Build.A.Block.TestObject;
            AddToMain(blockTree, block);
            blockTree.MoveToBranch(block.Hash);
            Block found = blockTree.FindBlock(block.Hash, false);
            Assert.AreSame(block, found);
        }

        [Test]
        public void Add_on_branch_and_not_find_on_main()
        {
            BlockTree blockTree = new BlockTree(OlympicSpecProvider.Instance, NullLogger.Instance);
            Block block = Build.A.Block.TestObject;
            blockTree.SuggestBlock(block);
            Block found = blockTree.FindBlock(block.Hash, true);
            Assert.IsNull(found);
        }

        [Test]
        public void Find_by_number_basic()
        {
            BlockTree blockTree = new BlockTree(OlympicSpecProvider.Instance, NullLogger.Instance);
            Block block0 = Build.A.Block.WithNumber(0).TestObject;
            Block block1 = Build.A.Block.WithNumber(1).WithParent(block0).TestObject;
            Block block2 = Build.A.Block.WithNumber(2).WithParent(block1).TestObject;
            AddToMain(blockTree, block0);
            AddToMain(blockTree, block1);
            AddToMain(blockTree, block2);

            Block found = blockTree.FindBlock(2);
            Assert.AreSame(block2, found);
        }

        [Test]
        public void Find_by_number_missing()
        {
            BlockTree blockTree = new BlockTree(OlympicSpecProvider.Instance, NullLogger.Instance);
            Block block0 = Build.A.Block.WithNumber(0).TestObject;
            Block block1 = Build.A.Block.WithNumber(1).WithParent(block0).TestObject;
            AddToMain(blockTree, block0);
            AddToMain(blockTree, block1);

            Block found = blockTree.FindBlock(5);
            Assert.IsNull(found);
        }

        [Test]
        public void Find_by_number_negative()
        {
            BlockTree blockTree = new BlockTree(OlympicSpecProvider.Instance, NullLogger.Instance);
            Block block0 = Build.A.Block.WithNumber(0).TestObject;
            Block block1 = Build.A.Block.WithNumber(1).WithParent(block0).TestObject;
            AddToMain(blockTree, block0);
            AddToMain(blockTree, block1);

            Assert.Throws<ArgumentException>(() => blockTree.FindBlock(-1));
        }

        [Test]
        public void Find_sequence_basic()
        {
            BlockTree blockTree = new BlockTree(OlympicSpecProvider.Instance, NullLogger.Instance);
            Block block0 = Build.A.Block.WithNumber(0).TestObject;
            Block block1 = Build.A.Block.WithNumber(1).WithParent(block0).TestObject;
            Block block2 = Build.A.Block.WithNumber(2).WithParent(block1).TestObject;
            AddToMain(blockTree, block0);
            AddToMain(blockTree, block1);
            AddToMain(blockTree, block2);

            Block[] blocks = blockTree.FindBlocks(block0.Hash, 3, 0, false);
            Assert.AreEqual(3, blocks.Length);
            Assert.AreSame(block0, blocks[0]);
            Assert.AreSame(block2, blocks[2]);
        }

        [Test]
        public void Find_sequence_reverse()
        {
            BlockTree blockTree = new BlockTree(OlympicSpecProvider.Instance, NullLogger.Instance);
            Block block0 = Build.A.Block.WithNumber(0).TestObject;
            Block block1 = Build.A.Block.WithNumber(1).WithParent(block0).TestObject;
            Block block2 = Build.A.Block.WithNumber(2).WithParent(block1).TestObject;
            AddToMain(blockTree, block0);
            AddToMain(blockTree, block1);
            AddToMain(blockTree, block2);

            Block[] blocks = blockTree.FindBlocks(block2.Hash, 3, 0, true);
            Assert.AreEqual(3, blocks.Length);
            Assert.AreSame(block2, blocks[0]);
            Assert.AreSame(block0, blocks[2]);
        }

        private static void AddToMain(BlockTree blockTree, Block block0)
        {
            blockTree.SuggestBlock(block0);
            blockTree.MarkAsProcessed(block0.Hash);
            blockTree.MoveToMain(block0.Hash);
        }

        [Test]
        public void Find_sequence_zero_blocks()
        {
            BlockTree blockTree = new BlockTree(OlympicSpecProvider.Instance, NullLogger.Instance);
            Block block0 = Build.A.Block.WithNumber(0).TestObject;
            Block block1 = Build.A.Block.WithNumber(1).WithParent(block0).TestObject;
            Block block2 = Build.A.Block.WithNumber(2).WithParent(block1).TestObject;
            AddToMain(blockTree, block0);
            AddToMain(blockTree, block1);
            AddToMain(blockTree, block2);

            Block[] blocks = blockTree.FindBlocks(block0.Hash, 0, 0, false);
            Assert.AreEqual(0, blocks.Length);
        }

        [Test]
        public void Find_sequence_basic_skip()
        {
            BlockTree blockTree = new BlockTree(OlympicSpecProvider.Instance, NullLogger.Instance);
            Block block0 = Build.A.Block.WithNumber(0).TestObject;
            Block block1 = Build.A.Block.WithNumber(1).WithParent(block0).TestObject;
            Block block2 = Build.A.Block.WithNumber(2).WithParent(block1).TestObject;
            AddToMain(blockTree, block0);
            AddToMain(blockTree, block1);
            AddToMain(blockTree, block2);

            Block[] blocks = blockTree.FindBlocks(block0.Hash, 2, 1, false);
            Assert.AreEqual(2, blocks.Length);
        }

        [Test]
        public void Find_sequence_some_empty()
        {
            BlockTree blockTree = new BlockTree(OlympicSpecProvider.Instance, NullLogger.Instance);
            Block block0 = Build.A.Block.WithNumber(0).TestObject;
            Block block1 = Build.A.Block.WithNumber(1).WithParent(block0).TestObject;
            Block block2 = Build.A.Block.WithNumber(2).WithParent(block1).TestObject;
            AddToMain(blockTree, block0);
            AddToMain(blockTree, block1);
            AddToMain(blockTree, block2);

            Block[] blocks = blockTree.FindBlocks(block0.Hash, 4, 0, false);
            Assert.AreEqual(4, blocks.Length);
            Assert.IsNull(blocks[3]);
        }

        [Test]
        public void Total_difficulty_is_calculated_when_exists_parent_with_total_difficulty()
        {
            BlockTree blockTree = new BlockTree(OlympicSpecProvider.Instance, NullLogger.Instance);
            
            Block block0 = Build.A.Block.WithNumber(0).WithDifficulty(1).TestObject;
            blockTree.SuggestBlock(block0);
            Block block1 = Build.A.Block.WithNumber(1).WithParentHash(block0.Hash).WithDifficulty(2).TestObject;
            blockTree.SuggestBlock(block1);
            Assert.AreEqual(3, (int)block1.TotalDifficulty);
        }

        [Test]
        public void Total_difficulty_is_null_when_no_parent()
        {
            BlockTree blockTree = new BlockTree(OlympicSpecProvider.Instance, NullLogger.Instance);
            
            Block block0 = Build.A.Block.WithNumber(0).WithDifficulty(1).TestObject;
            blockTree.SuggestBlock(block0);

            Block block2 = Build.A.Block.WithNumber(1).WithDifficulty(3).WithParentHash(Keccak.Zero).TestObject;
            blockTree.SuggestBlock(block2);
            Assert.AreEqual(null, block2.TotalDifficulty);
        }
        
        [Test]
        public void Head_block_gets_updated()
        {
            BlockTree blockTree = new BlockTree(OlympicSpecProvider.Instance, NullLogger.Instance);
            Block block0 = Build.A.Block.WithNumber(0).WithDifficulty(1).TestObject;
            Block block1 = Build.A.Block.WithNumber(1).WithDifficulty(2).WithParent(block0).TestObject;
            AddToMain(blockTree, block0);
            AddToMain(blockTree, block1); 

            Assert.AreEqual(block1, blockTree.HeadBlock);
        }
        
        [Test]
        public void Best_suggested_block_gets_updated()
        {
            BlockTree blockTree = new BlockTree(OlympicSpecProvider.Instance, NullLogger.Instance);
            Block block0 = Build.A.Block.WithNumber(0).WithDifficulty(1).TestObject;
            Block block1 = Build.A.Block.WithNumber(1).WithDifficulty(2).WithParent(block0).TestObject;
            AddToMain(blockTree, block0);
            blockTree.SuggestBlock(block1);

            Assert.AreEqual(block0, blockTree.HeadBlock, "head");
            Assert.AreEqual(block1, blockTree.BestSuggestedBlock, "best");
        }
        
        [Test]
        public void Sets_genesis_block()
        {
            BlockTree blockTree = new BlockTree(OlympicSpecProvider.Instance, NullLogger.Instance);
            Block block0 = Build.A.Block.WithNumber(0).WithDifficulty(1).TestObject;
            AddToMain(blockTree, block0);

            Assert.AreEqual(block0, blockTree.GenesisBlock);
        }
    }
}