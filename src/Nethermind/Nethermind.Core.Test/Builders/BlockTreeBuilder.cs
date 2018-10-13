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
using Nethermind.Blockchain.Validators;
using Nethermind.Core.Crypto;
using Nethermind.Core.Encoding;
using Nethermind.Core.Logging;
using Nethermind.Core.Specs;
using Nethermind.Store;
using NSubstitute;

namespace Nethermind.Core.Test.Builders
{
    public class BlockTreeBuilder : BuilderBase<BlockTree>
    {
        private readonly Block _genesisBlock;

        public BlockTreeBuilder()
            : this(Build.A.Block.Genesis.TestObject)
        {
        }

        public BlockTreeBuilder(Block genesisBlock)
        {
            MemDb blocksDb = new MemDb(); // so we automatically include in all tests my questionable decision of storing Head block header at 00...
            blocksDb.Set(Keccak.Zero, Rlp.Encode(Build.A.BlockHeader.TestObject).Bytes);
            
            _genesisBlock = genesisBlock;
            TestObjectInternal = new BlockTree(blocksDb, new MemDb(), RopstenSpecProvider.Instance, Substitute.For<ITransactionStore>(), NullLogManager.Instance);
        }

        public BlockTreeBuilder OfChainLength(int chainLength, int splitBlockNumber = 0, int splitVariant = 0)
        {
            Block previous = _genesisBlock;
            for (int i = 0; i < chainLength; i++)
            {
                TestObjectInternal.SuggestBlock(previous);
                TestObjectInternal.MarkAsProcessed(previous.Hash);
                TestObjectInternal.MoveToMain(previous.Hash);
                previous = Build.A.Block.WithNumber((ulong)i + 1).WithParent(previous).WithDifficulty(BlockHeaderBuilder.DefaultDifficulty - (ulong)splitVariant).TestObject;
            }

            return this;
        }

        public static void ExtendTree(IBlockTree blockTree, int newChainLength)
        {
            Block previous = blockTree.RetrieveHeadBlock();
            int initialLength = (int)previous.Number + 1;
            for (int i = initialLength; i < newChainLength; i++)
            {
                previous = Build.A.Block.WithNumber((ulong)i).WithParent(previous).TestObject;
                blockTree.SuggestBlock(previous);
                blockTree.MarkAsProcessed(previous.Hash);
                blockTree.MoveToMain(previous.Hash);
            }
        }
    }
}