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

using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Facade;
using Nethermind.Int256;
using Nethermind.JsonRpc.Modules.Eth;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.TxPool;
using Nethermind.Wallet;
using NSubstitute;
using NUnit.Framework;
using static Nethermind.JsonRpc.Test.Modules.BlockConstructor;

namespace Nethermind.JsonRpc.Test.Modules
{
    public partial class EthRpcModuleTests
    {
        [Test]
        public void Eth_gasPrice_WhenHeadBlockIsNull_ThrowsException()
        {
            IBlockFinder blockFinder = Substitute.For<IBlockFinder>();
            blockFinder.FindHeadBlock().Returns((Block) null);
            EthRpcModule testEthRpcModule = GetTestEthRpcModule(blockFinder: blockFinder);
            
            Action gasPriceCall = () => testEthRpcModule.eth_gasPrice();
            
            gasPriceCall.Should().Throw<Exception>().WithMessage("Head Block was not found.");
        }

        [Test]
        public void Eth_gasPrice_ForBlockTreeWithBlocks_CreatesMatchingBlockDict()
        {
            Block[] blocks = GetTwoTestBlocks();
            IBlockFinder blockFinder = Substitute.For<IBlockFinder>();
            EthRpcModule testEthRpcModule = GetTestEthRpcModule(blockFinder:blockFinder);
            blockFinder.FindBlock(0).Returns(blocks[0]);
            blockFinder.FindBlock(1).Returns(blocks[1]);
            blockFinder.FindHeadBlock().Returns(blocks[1]);
            Dictionary<long, Block> expected = new Dictionary<long, Block>
            {
                {0, blocks[0]},
                {1, blocks[1]}
            };
            
            testEthRpcModule.eth_gasPrice();
            
            testEthRpcModule.BlockNumberToBlockDictionary.Should().BeEquivalentTo(expected);
        }
        public Block[] GetTwoTestBlocks()
        {
            return GetBlocksFromKeyValuePairs(
                BlockNumberAndTxStringsKeyValuePair(0, CollectTxStrings(
                        GetTxString("A", "4", "0"), 
                        GetTxString("B", "3", "0"), 
                        GetTxString("C", "2", "0"), 
                        GetTxString("D", "1", "0")
                    )
                ), BlockNumberAndTxStringsKeyValuePair(1, CollectTxStrings(
                        GetTxString("A", "8", "1"), 
                        GetTxString("B", "7", "1"), 
                        GetTxString("C", "6", "1"), 
                        GetTxString("D", "5", "1")
                    )
                ));
        }

        [Test]
        public void Eth_gasPrice_GivenValidHeadBlock_CallsGasPriceEstimateFromGasPriceOracle()
        {
            IBlockFinder blockFinder = Substitute.For<IBlockFinder>();
            IGasPriceOracle gasPriceOracle = Substitute.For<IGasPriceOracle>();
            EthRpcModule testEthRpcModule = GetTestEthRpcModule(blockFinder, gasPriceOracle);
            Block testBlock = GetNoTxTestBlock();
            blockFinder.FindHeadBlock().Returns(testBlock);
            blockFinder.FindBlock(Arg.Is<long>(a => a == 0)).Returns(testBlock);

            testEthRpcModule.eth_gasPrice();
            
            gasPriceOracle.Received(1).GasPriceEstimate(Arg.Any<Block>(), Arg.Any<Dictionary<long, Block>>());
        }

        public static Block GetNoTxTestBlock()
        {
            return Build.A.Block.WithNumber(0).TestObject;
        }

        [Test]
        public void Eth_gasPrice_TxCountNotGreaterThanLimit_GetTxFromMoreBlocks()
        {
            Block[] blocks = GetBlocksFromKeyValuePairs(BlockNumberAndTxStringsKeyValuePair(0, CollectTxStrings(GetTxString("A", "0", "0"), GetTxString("B", "1", "0")
                    )
                ), BlockNumberAndTxStringsKeyValuePair(1, CollectTxStrings(GetTxString("C", "2", "0"), GetTxString("D", "3","0")
                    )
                ), BlockNumberAndTxStringsKeyValuePair(2, CollectTxStrings(GetTxString("A", "4", "0")
                    )
                ));

            BlockTreeSetup blockTreeSetup = new BlockTreeSetup(blocks: blocks, blockLimit: 2);
            ResultWrapper<UInt256?> resultWrapper = blockTreeSetup.EthRpcModule.eth_gasPrice();
            
            resultWrapper.Data.Should().Be((UInt256?) 2); 
            //Tx Gas Prices: 0,1,2,3,4, Index: (5-1) * 3/5 rounds to 2, Gas Price: 2
        }

        [Test]
        public void Eth_gasPrice_BlocksAvailableLessThanBlocksToCheck_ShouldBeSuccessful()
        {
            Block[] blocks = GetBlocksFromKeyValuePairs(BlockNumberAndTxStringsKeyValuePair(0, CollectTxStrings(GetTxString("A", "3", "0"), GetTxString("B", "4", "0")
                    )
                ), BlockNumberAndTxStringsKeyValuePair(1, CollectTxStrings(GetTxString("C", "5", "0"), GetTxString("D", "6","0")
                    )
                ), BlockNumberAndTxStringsKeyValuePair(2, CollectTxStrings(GetTxString("A", "7", "0"), GetTxString("B", "8", "1")
                    )
                ));


            BlockTreeSetup blockTreeSetup = new BlockTreeSetup(blocks: blocks, blockLimit: 4);
            ResultWrapper<UInt256?> resultWrapper = blockTreeSetup.EthRpcModule.eth_gasPrice();
            
            resultWrapper.Result.Should().Be(Result.Success); 
        }
        
        [Test]
        public void Eth_gasPrice_GetTxFromMinBlocks_NumTxInMinBlocksGreaterThanOrEqualToLimit()
        {
            Block[] blocks = GetBlocksFromKeyValuePairs(BlockNumberAndTxStringsKeyValuePair(0, CollectTxStrings(GetTxString("A", "1", "0"), GetTxString("B", "2", "0")
                    )
                ), BlockNumberAndTxStringsKeyValuePair(1, CollectTxStrings(GetTxString("C", "3", "0"), GetTxString("D", "4", "0")
                    )
                ), BlockNumberAndTxStringsKeyValuePair(2, CollectTxStrings(GetTxString("A", "5","1"), GetTxString("B", "6","1")
                    )
                )
            );
            BlockTreeSetup blockTreeSetup = new BlockTreeSetup(blocks: blocks, blockLimit: 2);
            
            blockTreeSetup.EthRpcModule.eth_gasPrice();
            
            List<UInt256> expected = new List<UInt256>{3, 4, 5, 6};
            blockTreeSetup.GasPriceOracle.TxGasPriceList.Should().Equal(expected);
        }
        
        [Test]
        public void Eth_gasPrice_TransactionSentByMiner_AreNotConsideredInGasPriceCalculation()
        {
            Address minerAddress = PrivateKeyForLetter('A').Address;
            Block block = GetBlockWithBeneficiaryBlockNumberAndTxInfo(minerAddress, 0, CollectTxStrings(GetTxString("A", "7", "0"), GetTxString("B", "8", "0"), GetTxString("C", "9", "0")
                    )
                );
            BlockTreeSetup blockTreeSetup = new BlockTreeSetup(new[]{block});
            blockTreeSetup.EthRpcModule.eth_gasPrice();

            List<UInt256> gasPriceList = blockTreeSetup.GasPriceOracle.TxGasPriceList;
            List<UInt256> expected = new List<UInt256>{8,9};
            gasPriceList.Should().Equal(expected);
        }


        private EthRpcModule GetTestEthRpcModule(IBlockFinder blockFinder = null, IGasPriceOracle gasPriceOracle = null)
        {
            return new EthRpcModule
            (
                Substitute.For<IJsonRpcConfig>(),
                Substitute.For<IBlockchainBridge>(),
                blockFinder ?? Substitute.For<IBlockFinder>(),
                Substitute.For<IStateReader>(),
                Substitute.For<ITxPool>(),
                Substitute.For<ITxSender>(),
                Substitute.For<IWallet>(),
                Substitute.For<ILogManager>(),
                Substitute.For<ISpecProvider>(),
                gasPriceOracle ?? Substitute.For<IGasPriceOracle>()
            );
        }

        public class BlockTreeSetup
        {
            public Block[] Blocks { get; private set; }
            private BlockTree BlockTree { get; set; }
            public EthRpcModule EthRpcModule { get; }
            public IGasPriceOracle GasPriceOracle { get; private set; }

            public BlockTreeSetup(
                Block[] blocks = null,
                bool addBlocks = false,
                IGasPriceOracle gasPriceOracle = null, 
                int? blockLimit = null,
                UInt256? ignoreUnder = null,
                UInt256? baseFee = null,
                bool eip1559Enabled = false,
                ITxInsertionManager txInsertionManager = null,
                IHeadBlockChangeManager headBlockChangeManager = null)
            {
                GetBlocks(blocks, addBlocks);

                InitializeAndAddToBlockTree();

                GasPriceOracle = gasPriceOracle ?? GetGasPriceOracle(eip1559Enabled, ignoreUnder, blockLimit, baseFee, txInsertionManager, headBlockChangeManager);

                EthRpcModule = new EthRpcModuleTests().GetTestEthRpcModule(BlockTree, GasPriceOracle);
            }

            private void InitializeAndAddToBlockTree()
            {
                BlockTree = BuildABlockTreeWithGenesisBlock(Blocks[0]);
                foreach (Block block in Blocks)
                {
                    BlockTreeBuilder.AddBlock(BlockTree, block);
                }
            }

            private void GetBlocks(Block[] blocks, bool shouldAddToBlocks)
            {
                if (NoBlocksGiven(blocks) || shouldAddToBlocks)
                {
                    GetBlockArray();
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
            
            private static bool NoBlocksGiven(Block[] blocks)
            {
                return blocks == null;
            }

            private BlockTree BuildABlockTreeWithGenesisBlock(Block genesisBlock)
            {
                return Build.A.BlockTree(genesisBlock).TestObject;
            }

            private void GetBlockArray()
            {
                Blocks = GetBlocksFromKeyValuePairs(
                    BlockNumberAndTxStringsKeyValuePair(0, CollectTxStrings(
                            GetTxString("A", "1", "0"),
                            GetTxString("B", "2", "0")
                        )
                    ),
                    BlockNumberAndTxStringsKeyValuePair(1, CollectTxStrings(
                            GetTxString("C", "3", "0")
                        )
                    ),
                    BlockNumberAndTxStringsKeyValuePair(2, CollectTxStrings(
                            GetTxString("D", "5", "0")
                        )
                    ),
                    BlockNumberAndTxStringsKeyValuePair(3, CollectTxStrings(
                            GetTxString("A", "4", "1")
                        )
                    ),
                    BlockNumberAndTxStringsKeyValuePair(4, CollectTxStrings(
                            GetTxString("B", "6", "1")
                        )
                    )
                );
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
            
            private IGasPriceOracle GetGasPriceOracle(
                bool eip1559Enabled, 
                UInt256? ignoreUnder,
                int? blockLimit, 
                UInt256? baseFee,
                ITxInsertionManager txInsertionManager,
                IHeadBlockChangeManager headBlockChangeManager)
            {
                GasPriceOracle gasPriceOracle = new GasPriceOracle(eip1559Enabled, ignoreUnder, blockLimit, baseFee, 
                    txInsertionManager, headBlockChangeManager);
                return gasPriceOracle;
            }
        }
    }
}
