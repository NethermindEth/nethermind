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
using Nethermind.Core.Crypto;
using NUnit.Framework;

namespace Nethermind.Core.Test
{
    [TestFixture]
    public class BlockStoreTests
    {
        [Test]
        public void Add_and_find_on_main()
        {
            BlockStore blockStore = new BlockStore();
            Block block = BuildTestBlock();
            blockStore.AddBlock(block, true);
            Block found = blockStore.FindBlock(block.Hash, true);
            Assert.AreSame(block, found);
        }

        [Test]
        public void Add_and_find_branch()
        {
            BlockStore blockStore = new BlockStore();
            Block block = BuildTestBlock();
            blockStore.AddBlock(block, false);
            Block found = blockStore.FindBlock(block.Hash, false);
            Assert.AreSame(block, found);
        }

        [Test]
        public void Add_on_branch_move_find()
        {
            BlockStore blockStore = new BlockStore();
            Block block = BuildTestBlock();
            blockStore.AddBlock(block, false);
            blockStore.MoveToMain(block.Hash);
            Block found = blockStore.FindBlock(block.Hash, true);
            Assert.AreSame(block, found);
        }
        
        [Test]
        public void Add_on_main_move_find()
        {
            BlockStore blockStore = new BlockStore();
            Block block = BuildTestBlock();
            blockStore.AddBlock(block, true);
            blockStore.MoveToBranch(block.Hash);
            Block found = blockStore.FindBlock(block.Hash, false);
            Assert.AreSame(block, found);
        }
        
        [Test]
        public void Add_on_branch_and_not_find_on_main()
        {
            BlockStore blockStore = new BlockStore();
            Block block = BuildTestBlock();
            blockStore.AddBlock(block, false);
            Block found = blockStore.FindBlock(block.Hash, true);
            Assert.IsNull(found);
        }

        private static Block BuildTestBlock()
        {
            BlockHeader header = new BlockHeader(Keccak.Compute("parent"), Keccak.OfAnEmptySequenceRlp, new Address(Keccak.Compute("address")), 0, 0, 0, 0, new byte[0]);
            header.Bloom = new Bloom();
            header.GasUsed = 0;
            header.MixHash = Keccak.Compute("mix hash");
            header.Nonce = 0;
            header.OmmersHash = Keccak.OfAnEmptySequenceRlp;
            header.ReceiptsRoot = Keccak.EmptyTreeHash;
            header.StateRoot = Keccak.EmptyTreeHash;
            header.TransactionsRoot = Keccak.EmptyTreeHash;
            header.RecomputeHash();
            Block block = new Block(header);
            return block;
        }
    }
}