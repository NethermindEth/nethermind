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
using Nethermind.Core.Specs;

namespace Nethermind.Core.Test.Builders
{
    public class BlockTreeBuilder : BuilderBase<BlockTree>
    {
        private readonly Block _genesisBlock;

        public BlockTreeBuilder(Block genesisBlock)
        {
            _genesisBlock = genesisBlock;
            TestObjectInternal = new BlockTree(RopstenSpecProvider.Instance, NullLogger.Instance);
        }

        public BlockTreeBuilder OfChainLength(int chainLength, int splitBlockNumber = 0, int splitVariant = 0)
        {
            Block previous = _genesisBlock;
            for (int i = 0; i < chainLength; i++)
            {
                TestObjectInternal.SuggestBlock(previous);
                TestObjectInternal.MarkAsProcessed(previous.Hash);
                TestObjectInternal.MoveToMain(previous.Hash);
                previous = Build.A.Block.WithNumber(i + 1).WithParent(previous).WithDifficulty(BlockHeaderBuilder.DefaultDifficulty - splitVariant).TestObject;
            }
            
            return this;
        }

        public static void ExtendTree(IBlockTree blockTree, int newChainLength)
        {
            Block previous = blockTree.HeadBlock;
            int initialLength = (int)previous.Number + 1;
            for (int i = initialLength; i < newChainLength; i++)
            {
                previous = Build.A.Block.WithNumber(i).WithParent(previous).TestObject;
                blockTree.SuggestBlock(previous);
                blockTree.MarkAsProcessed(previous.Hash);
                blockTree.MoveToMain(previous.Hash);
            }
        }
    }
}