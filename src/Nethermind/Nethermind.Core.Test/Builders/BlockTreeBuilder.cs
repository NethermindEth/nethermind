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
using Nethermind.Blockchain.TxPools;
using Nethermind.Blockchain.Validators;
using Nethermind.Core.Crypto;
using Nethermind.Core.Encoding;
using Nethermind.Core.Specs;
using Nethermind.Logging;
using Nethermind.Store;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Core.Test.Builders
{
    public class BlockTreeBuilder : BuilderBase<BlockTree>
    {
        private readonly Block _genesisBlock;

        private bool _onlyHeaders;

        public BlockTreeBuilder()
            : this(Build.A.Block.Genesis.TestObject)
        {
        }

        public BlockTreeBuilder(Block genesisBlock)
        {
            MemDb blocksDb = new MemDb();
            MemDb headersDb = new MemDb();
            // so we automatically include in all tests my questionable decision of storing Head block header at 00...
            blocksDb.Set(Keccak.Zero, Rlp.Encode(Build.A.BlockHeader.TestObject).Bytes);

            _genesisBlock = genesisBlock;
            TestObjectInternal = new BlockTree(blocksDb, headersDb, new MemDb(), RopstenSpecProvider.Instance, Substitute.For<ITxPool>(), NullLogManager.Instance);
        }

        public BlockTreeBuilder OfHeadersOnly
        {
            get
            {
                _onlyHeaders = true;
                return this;
            }
        }

        public BlockTreeBuilder OfChainLength(int chainLength, int splitVariant = 0)
        {
            OfChainLength(out _, chainLength, splitVariant);
            return this;
        }

        public BlockTreeBuilder OfChainLength(out Block headBlock, int chainLength, int splitVariant = 0)
        {
            Block current = _genesisBlock;
            headBlock = _genesisBlock;

            bool skipGenesis = TestObjectInternal.Head != null;
            for (int i = 0; i < chainLength; i++)
            {
                headBlock = current;
                if (_onlyHeaders)
                {
                    if (!(current.IsGenesis && skipGenesis))
                    {
                        TestObjectInternal.SuggestHeader(current.Header);
                    }

                    current = Build.A.Block.WithNumber(i + 1).WithParent(current).WithDifficulty(BlockHeaderBuilder.DefaultDifficulty - (ulong) splitVariant).TestObject;
                }
                else
                {
                    if (!(current.IsGenesis && skipGenesis))
                    {
                        AddBlockResult result = TestObjectInternal.SuggestBlock(current);
                        Assert.AreEqual(AddBlockResult.Added, result, $"Adding {current.ToString(Block.Format.Short)} at split variant {splitVariant}");
                        
                        TestObjectInternal.UpdateMainChain(current);
                    }

                    current = Build.A.Block.WithNumber(i + 1).WithParent(current).WithDifficulty(BlockHeaderBuilder.DefaultDifficulty - (ulong) splitVariant).TestObject;
                }
            }

            return this;
        }

        public BlockTreeBuilder WithOnlySomeBlocksProcessed(int chainLength, int processedChainLength)
        {
            Block current = _genesisBlock;
            for (int i = 0; i < chainLength; i++)
            {
                TestObjectInternal.SuggestBlock(current);
                if (current.Number < processedChainLength)
                {
                    TestObjectInternal.UpdateMainChain(current);
                }

                current = Build.A.Block.WithNumber(i + 1).WithParent(current).WithDifficulty(BlockHeaderBuilder.DefaultDifficulty).TestObject;
            }

            return this;
        }

        public static void ExtendTree(IBlockTree blockTree, int newChainLength)
        {
            Block previous = blockTree.RetrieveHeadBlock();
            int initialLength = (int) previous.Number + 1;
            for (int i = initialLength; i < newChainLength; i++)
            {
                previous = Build.A.Block.WithNumber(i).WithParent(previous).TestObject;
                blockTree.SuggestBlock(previous);
                blockTree.UpdateMainChain(new[] {previous});
            }
        }
    }
}