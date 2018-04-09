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

using Nethermind.Blockchain;
using Nethermind.Core.Test.Builders;
using NUnit.Framework;

namespace Nethermind.Core.Test
{
    [TestFixture]
    public class BlockTreeTests
    {
        [Test]
        public void Add_and_find_on_main()
        {
            BlockTree blockStore = new BlockTree();
            Block block = Build.A.Block.TestObject;
            blockStore.AddBlock(block, true);
            Block found = blockStore.FindBlock(block.Hash, true);
            Assert.AreSame(block, found);
        }

        [Test]
        public void Add_and_find_branch()
        {
            BlockTree blockStore = new BlockTree();
            Block block = Build.A.Block.TestObject;
            blockStore.AddBlock(block, false);
            Block found = blockStore.FindBlock(block.Hash, false);
            Assert.AreSame(block, found);
        }

        [Test]
        public void Add_on_branch_move_find()
        {
            BlockTree blockStore = new BlockTree();
            Block block = Build.A.Block.TestObject;
            blockStore.AddBlock(block, false);
            blockStore.MoveToMain(block.Hash);
            Block found = blockStore.FindBlock(block.Hash, true);
            Assert.AreSame(block, found);
        }
        
        [Test]
        public void Add_on_main_move_find()
        {
            BlockTree blockStore = new BlockTree();
            Block block = Build.A.Block.TestObject;
            blockStore.AddBlock(block, true);
            blockStore.MoveToBranch(block.Hash);
            Block found = blockStore.FindBlock(block.Hash, false);
            Assert.AreSame(block, found);
        }
        
        [Test]
        public void Add_on_branch_and_not_find_on_main()
        {
            BlockTree blockStore = new BlockTree();
            Block block = Build.A.Block.TestObject;
            blockStore.AddBlock(block, false);
            Block found = blockStore.FindBlock(block.Hash, true);
            Assert.IsNull(found);
        }
    }
}