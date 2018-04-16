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
            BlockTree blockStore = new BlockTree(ChainId.Olympic, NullLogger.Instance);
            blockStore.NewBestBlockSuggested += (sender, args) => { hasNotified = true; };

            Block block = Build.A.Block.WithNumber(0).TestObject;
            var result = blockStore.AddBlock(block);

            Assert.True(hasNotified, "notification");
            Assert.AreEqual(AddBlockResult.Added, result, "result");
        }

        [Test]
        public void Can_only_add_genesis_once()
        {
            BlockTree blockStore = new BlockTree(ChainId.Olympic, NullLogger.Instance);
            Block blockA = Build.A.Block.WithNumber(0).TestObject;
            Block blockB = Build.A.Block.WithNumber(0).TestObject;
            blockStore.AddBlock(blockA);
            Assert.Throws<InvalidOperationException>(() => blockStore.AddBlock(blockB));
        }

        [Test]
        public void Shall_notify_on_block_after_genesis()
        {
            bool hasNotified = false;
            BlockTree blockStore = new BlockTree(ChainId.Olympic, NullLogger.Instance);
            Block block0 = Build.A.Block.WithNumber(0).WithDifficulty(1).TestObject;
            Block block1 = Build.A.Block.WithNumber(1).WithDifficulty(2).WithParent(block0).TestObject;
            blockStore.AddBlock(block0);
            blockStore.NewBestBlockSuggested += (sender, args) => { hasNotified = true; };
            var result = blockStore.AddBlock(block1);

            Assert.True(hasNotified, "notification");
            Assert.AreEqual(AddBlockResult.Added, result, "result");
        }

        [Test]
        public void Shall_not_notify_but_add_on_lower_difficulty()
        {
            bool hasNotified = false;
            BlockTree blockStore = new BlockTree(ChainId.Olympic, NullLogger.Instance);
            Block block0 = Build.A.Block.WithNumber(0).WithDifficulty(1).TestObject;
            Block block1 = Build.A.Block.WithNumber(1).WithDifficulty(3).WithParent(block0).TestObject;
            Block block2 = Build.A.Block.WithNumber(1).WithDifficulty(2).WithParent(block0).TestObject;
            blockStore.AddBlock(block0);
            blockStore.AddBlock(block1);
            blockStore.NewBestBlockSuggested += (sender, args) => { hasNotified = true; };
            var result = blockStore.AddBlock(block2);

            Assert.False(hasNotified, "notification");
            Assert.AreEqual(AddBlockResult.Added, result, "result");
        }

        [Test]
        public void Shall_ignore_orphans()
        {
            BlockTree blockStore = new BlockTree(ChainId.Olympic, NullLogger.Instance);
            Block block0 = Build.A.Block.WithNumber(0).WithDifficulty(1).TestObject;
            Block block2 = Build.A.Block.WithNumber(2).WithDifficulty(3).TestObject;
            blockStore.AddBlock(block0);
            var result = blockStore.AddBlock(block2);
            Assert.AreEqual(AddBlockResult.Ignored, result);
        }

        [Test]
        public void Add_and_find_branch()
        {
            BlockTree blockStore = new BlockTree(ChainId.Olympic, NullLogger.Instance);
            Block block = Build.A.Block.TestObject;
            blockStore.AddBlock(block);
            Block found = blockStore.FindBlock(block.Hash, false);
            Assert.AreSame(block, found);
        }

        [Test]
        public void Add_on_branch_move_find()
        {
            BlockTree blockStore = new BlockTree(ChainId.Olympic, NullLogger.Instance);
            Block block = Build.A.Block.TestObject;
            blockStore.AddBlock(block);
            blockStore.MoveToMain(block.Hash);
            Block found = blockStore.FindBlock(block.Hash, true);
            Assert.AreSame(block, found);
        }

        [Test]
        public void Add_on_main_move_find()
        {
            BlockTree blockStore = new BlockTree(ChainId.Olympic, NullLogger.Instance);
            Block block = Build.A.Block.TestObject;
            blockStore.AddBlock(block);
            blockStore.MoveToMain(block.Hash);
            blockStore.MoveToBranch(block.Hash);
            Block found = blockStore.FindBlock(block.Hash, false);
            Assert.AreSame(block, found);
        }

        [Test]
        public void Add_on_branch_and_not_find_on_main()
        {
            BlockTree blockStore = new BlockTree(ChainId.Olympic, NullLogger.Instance);
            Block block = Build.A.Block.TestObject;
            blockStore.AddBlock(block);
            Block found = blockStore.FindBlock(block.Hash, true);
            Assert.IsNull(found);
        }

        [Test]
        public void Find_by_number_basic()
        {
            BlockTree blockStore = new BlockTree(ChainId.Olympic, NullLogger.Instance);
            Block block0 = Build.A.Block.WithNumber(0).TestObject;
            Block block1 = Build.A.Block.WithNumber(1).WithParent(block0).TestObject;
            Block block2 = Build.A.Block.WithNumber(2).WithParent(block1).TestObject;
            blockStore.AddBlock(block0);
            blockStore.MoveToMain(block0.Hash);
            blockStore.AddBlock(block1);
            blockStore.MoveToMain(block1.Hash);
            blockStore.AddBlock(block2);
            blockStore.MoveToMain(block2.Hash);

            Block found = blockStore.FindBlock(2);
            Assert.AreSame(block2, found);
        }

        [Test]
        public void Find_by_number_missing()
        {
            BlockTree blockStore = new BlockTree(ChainId.Olympic, NullLogger.Instance);
            Block block0 = Build.A.Block.WithNumber(0).TestObject;
            Block block1 = Build.A.Block.WithNumber(1).WithParent(block0).TestObject;
            blockStore.AddBlock(block0);
            blockStore.MoveToMain(block0.Hash);
            blockStore.AddBlock(block1);
            blockStore.MoveToMain(block1.Hash);

            Block found = blockStore.FindBlock(5);
            Assert.IsNull(found);
        }

        [Test]
        public void Find_by_number_negative()
        {
            BlockTree blockStore = new BlockTree(ChainId.Olympic, NullLogger.Instance);
            Block block0 = Build.A.Block.WithNumber(0).TestObject;
            Block block1 = Build.A.Block.WithNumber(1).WithParent(block0).TestObject;
            blockStore.AddBlock(block0);
            blockStore.MoveToMain(block0.Hash);
            blockStore.AddBlock(block1);
            blockStore.MoveToMain(block1.Hash);

            Assert.Throws<ArgumentException>(() => blockStore.FindBlock(-1));
        }

        [Test]
        public void Find_sequence_basic()
        {
            BlockTree blockStore = new BlockTree(ChainId.Olympic, NullLogger.Instance);
            Block block0 = Build.A.Block.WithNumber(0).TestObject;
            Block block1 = Build.A.Block.WithNumber(1).WithParent(block0).TestObject;
            Block block2 = Build.A.Block.WithNumber(2).WithParent(block1).TestObject;
            blockStore.AddBlock(block0);
            blockStore.MoveToMain(block0.Hash);
            blockStore.AddBlock(block1);
            blockStore.MoveToMain(block1.Hash);
            blockStore.AddBlock(block2);
            blockStore.MoveToMain(block2.Hash);

            Block[] blocks = blockStore.FindBlocks(block0.Hash, 3, 0, false);
            Assert.AreEqual(3, blocks.Length);
            Assert.AreSame(block0, blocks[0]);
            Assert.AreSame(block2, blocks[2]);
        }

        [Test]
        public void Find_sequence_reverse()
        {
            BlockTree blockStore = new BlockTree(ChainId.Olympic, NullLogger.Instance);
            Block block0 = Build.A.Block.WithNumber(0).TestObject;
            Block block1 = Build.A.Block.WithNumber(1).WithParent(block0).TestObject;
            Block block2 = Build.A.Block.WithNumber(2).WithParent(block1).TestObject;
            blockStore.AddBlock(block0);
            blockStore.MoveToMain(block0.Hash);
            blockStore.AddBlock(block1);
            blockStore.MoveToMain(block1.Hash);
            blockStore.AddBlock(block2);
            blockStore.MoveToMain(block2.Hash);

            Block[] blocks = blockStore.FindBlocks(block2.Hash, 3, 0, true);
            Assert.AreEqual(3, blocks.Length);
            Assert.AreSame(block2, blocks[0]);
            Assert.AreSame(block0, blocks[2]);
        }

        [Test]
        public void Find_sequence_zero_blocks()
        {
            BlockTree blockStore = new BlockTree(ChainId.Olympic, NullLogger.Instance);
            Block block0 = Build.A.Block.WithNumber(0).TestObject;
            Block block1 = Build.A.Block.WithNumber(1).WithParent(block0).TestObject;
            Block block2 = Build.A.Block.WithNumber(2).WithParent(block1).TestObject;
            blockStore.AddBlock(block0);
            blockStore.MoveToMain(block0.Hash);
            blockStore.AddBlock(block1);
            blockStore.MoveToMain(block1.Hash);
            blockStore.AddBlock(block2);
            blockStore.MoveToMain(block2.Hash);

            Block[] blocks = blockStore.FindBlocks(block0.Hash, 0, 0, false);
            Assert.AreEqual(0, blocks.Length);
        }

        [Test]
        public void Find_sequence_basic_skip()
        {
            BlockTree blockStore = new BlockTree(ChainId.Olympic, NullLogger.Instance);
            Block block0 = Build.A.Block.WithNumber(0).TestObject;
            Block block1 = Build.A.Block.WithNumber(1).WithParent(block0).TestObject;
            Block block2 = Build.A.Block.WithNumber(2).WithParent(block1).TestObject;
            blockStore.AddBlock(block0);
            blockStore.MoveToMain(block0.Hash);
            blockStore.AddBlock(block1);
            blockStore.MoveToMain(block1.Hash);
            blockStore.AddBlock(block2);
            blockStore.MoveToMain(block2.Hash);

            Block[] blocks = blockStore.FindBlocks(block0.Hash, 2, 1, false);
            Assert.AreEqual(2, blocks.Length);
        }

        [Test]
        public void Find_sequence_some_empty()
        {
            BlockTree blockStore = new BlockTree(ChainId.Olympic, NullLogger.Instance);
            Block block0 = Build.A.Block.WithNumber(0).TestObject;
            Block block1 = Build.A.Block.WithNumber(1).WithParent(block0).TestObject;
            Block block2 = Build.A.Block.WithNumber(2).WithParent(block1).TestObject;
            blockStore.AddBlock(block0);
            blockStore.MoveToMain(block0.Hash);
            blockStore.AddBlock(block1);
            blockStore.MoveToMain(block1.Hash);
            blockStore.AddBlock(block2);
            blockStore.MoveToMain(block2.Hash);

            Block[] blocks = blockStore.FindBlocks(block0.Hash, 4, 0, false);
            Assert.AreEqual(4, blocks.Length);
            Assert.IsNull(blocks[3]);
        }

        [Test]
        public void Total_difficulty_is_calculated_when_exists_parent_with_total_difficulty()
        {
            BlockTree blockStore = new BlockTree(ChainId.Olympic, NullLogger.Instance);
            ;
            Block block0 = Build.A.Block.WithNumber(0).WithDifficulty(1).TestObject;
            blockStore.AddBlock(block0);
            Block block1 = Build.A.Block.WithNumber(1).WithParentHash(block0.Hash).WithDifficulty(2).TestObject;
            blockStore.AddBlock(block1);
            Assert.AreEqual(3, (int)block1.TotalDifficulty);
        }

        [Test]
        public void Total_difficulty_is_null_when_no_parent()
        {
            BlockTree blockStore = new BlockTree(ChainId.Olympic, NullLogger.Instance);
            ;
            Block block0 = Build.A.Block.WithNumber(0).WithDifficulty(1).TestObject;
            blockStore.AddBlock(block0);

            Block block2 = Build.A.Block.WithNumber(1).WithDifficulty(3).WithParentHash(Keccak.Zero).TestObject;
            blockStore.AddBlock(block2);
            Assert.AreEqual(null, block2.TotalDifficulty);
        }
    }
}