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

using System.Collections.Generic;
using System.Linq;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.JsonRpc.Modules.Eth;
using NSubstitute;
using NSubstitute.Extensions;

namespace Nethermind.JsonRpc.Test.Modules.Eth
{
    public partial class EthRpcModuleTests
    {
        public class BlockTreeSetup
        {
            private Block[] Blocks { get; set; }
            public BlockTree BlockTree { get; private set; }
            private EthRpcModule EthRpcModule { get; }
            public IGasPriceOracle GasPriceOracle { get; }

            private static readonly Block[] _defaultBlocks = GetBlockArray();

            public BlockTreeSetup(
                ISpecProvider specProvider,
                Block[] blocks = null,
                bool addBlocks = false,
                IGasPriceOracle gasPriceOracle = null,
                int? blockLimit = null,
                UInt256? ignoreUnder = null,
                ITxInsertionManager txInsertionManager = null)
            {
                GetBlocks(blocks, addBlocks);

                InitializeAndAddToBlockTree(BlockTree);

                GasPriceOracle = gasPriceOracle ?? GetGasPriceOracle(specProvider, txInsertionManager, blockLimit, ignoreUnder);

                EthRpcModule = GetTestEthRpcModule(BlockTree, GasPriceOracle);
            }


            private void InitializeAndAddToBlockTree(IBlockTree blockTree)
            {
                BlockTree = BuildABlockTreeWithGenesisBlock(Blocks[0]);
                foreach (Block block in Blocks)
                {
                    BlockTreeBuilder.AddBlock(BlockTree, block);
                }
            }

            private void GetBlocks(Block[] blocks, bool shouldAddToBlocks)
            {
                if (blocks == null || shouldAddToBlocks)
                {
                    Blocks = _defaultBlocks;
                    if (shouldAddToBlocks)
                    {
                        AddExtraBlocksToArray(blocks);
                    }
                }
                else
                {
                    Blocks = blocks;
                }
            }

            private BlockTree BuildABlockTreeWithGenesisBlock(Block genesisBlock)
            {
                return Build.A.BlockTree(genesisBlock).TestObject;
            }

            private static Block[] GetBlockArray()
            {
                Block firstBlock = Build.A.Block.WithNumber(0).WithParentHash(Keccak.Zero).WithTransactions(
                        Build.A.Transaction.WithGasPrice(1).SignedAndResolved(TestItem.PrivateKeyA).WithNonce(0)
                            .TestObject,
                        Build.A.Transaction.WithGasPrice(2).SignedAndResolved(TestItem.PrivateKeyB).WithNonce(0)
                            .TestObject)
                    .TestObject;
                Block secondBlock = Build.A.Block.WithNumber(2).WithParentHash(firstBlock.Hash!).WithTransactions(
                        Build.A.Transaction.WithGasPrice(3).SignedAndResolved(TestItem.PrivateKeyC).WithNonce(0)
                            .TestObject)
                    .TestObject;
                Block thirdBlock = Build.A.Block.WithNumber(3).WithParentHash(secondBlock.Hash!).WithTransactions(
                        Build.A.Transaction.WithGasPrice(5).SignedAndResolved(TestItem.PrivateKeyD).WithNonce(0)
                            .TestObject)
                    .TestObject;
                Block fourthBlock = Build.A.Block.WithNumber(4).WithParentHash(thirdBlock.Hash!).WithTransactions(
                        Build.A.Transaction.WithGasPrice(4).SignedAndResolved(TestItem.PrivateKeyA).WithNonce(1)
                            .TestObject)
                    .TestObject;
                Block fifthBlock = Build.A.Block.WithNumber(5).WithParentHash(fourthBlock.Hash!).WithTransactions(
                        Build.A.Transaction.WithGasPrice(6).SignedAndResolved(TestItem.PrivateKeyB).WithNonce(1)
                            .TestObject)
                    .TestObject;
                return new[] {firstBlock, secondBlock, thirdBlock, fourthBlock, fifthBlock};
            }

            private void AddExtraBlocksToArray(Block[] blocks)
            {
                List<Block> listBlocks = Blocks.ToList();
                foreach (Block block in blocks)
                {
                    listBlocks.Add(block);
                }

                Blocks = listBlocks.ToArray();
            }
            
            private IGasPriceOracle GetGasPriceOracle(ISpecProvider specProvider, ITxInsertionManager txInsertionManager, int? blockLimit, UInt256? ignoreUnder)
            {
                GasPriceOracle gasPriceOracle = Substitute.For<GasPriceOracle>(specProvider, txInsertionManager);
                if (blockLimit != null)
                {
                    gasPriceOracle.Configure().GetBlockLimit().Returns((int) blockLimit);
                }

                if (ignoreUnder != null)
                {
                    gasPriceOracle.Configure().GetIgnoreUnder().Returns((UInt256) ignoreUnder);
                }

                return gasPriceOracle;
            }
        }
    }
}
