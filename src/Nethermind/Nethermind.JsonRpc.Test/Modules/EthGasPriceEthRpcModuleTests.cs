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
            return BlockConstructor.GetBlocksFromKeyValuePairs(BlockConstructor.BlockNumberAndTxStringsKeyValuePair(0, BlockConstructor.CollectTxStrings(BlockConstructor.GetTxString("A", "4", "0"), BlockConstructor.GetTxString("B", "3", "0"), BlockConstructor.GetTxString("C", "2", "0"), BlockConstructor.GetTxString("D", "1", "0")
                    )
                ), BlockConstructor.BlockNumberAndTxStringsKeyValuePair(1, BlockConstructor.CollectTxStrings(BlockConstructor.GetTxString("A", "8", "1"), BlockConstructor.GetTxString("B", "7", "1"), BlockConstructor.GetTxString("C", "6", "1"), BlockConstructor.GetTxString("D", "5", "1")
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
        public void Eth_gasPrice_DuplicateGasPrices_ReturnsSuccessfully()
        {
            BlockTreeSetup blockTreeSetup = new BlockTreeSetup();
            Transaction[] dupGasPriceGroup = BlockConstructor.GetTransactionsFromTxStrings(BlockConstructor.CollectTxStrings(BlockConstructor.GetTxString("B","6","2"), BlockConstructor.GetTxString("B","6","3"), BlockConstructor.GetTxString("B","6","4")),IsNotEip1559());
            Block dupGasPriceBlock = BlockConstructor.GetBlockWithNumberParentHashAndTxInfo(5, HashOfLastBlockIn(blockTreeSetup),
                dupGasPriceGroup);
            blockTreeSetup = new BlockTreeSetup(new[]{dupGasPriceBlock},true);

            ResultWrapper<UInt256?> resultWrapper = blockTreeSetup.EthRpcModule.eth_gasPrice();

            resultWrapper.Result.Should().Be(Result.Success);
        }
       //// 
        [Test]
        public void Eth_gasPrice_BlockcountEqualToBlocksToCheck_ShouldGetSixtiethPercentileIndex()
        {
            BlockTreeSetup blockTreeSetup = new BlockTreeSetup();
            ResultWrapper<UInt256?> resultWrapper = blockTreeSetup.EthRpcModule.eth_gasPrice();
            resultWrapper.Data.Should().Be((UInt256?) 4); 
            //Tx Gas Prices: 1,2,3,4,5,6, Index: (6-1) * 3/5 = 3, Gas Price: 4
        }

        [Test]
        public void Eth_gasPrice_EstimatedGasPriceMoreThanMaxGasPrice_ReturnMaxGasPrice()
        {
            Block[] blockArray = BlockConstructor.GetBlocksFromKeyValuePairs(BlockConstructor.BlockNumberAndTxStringsKeyValuePair(0, BlockConstructor.CollectTxStrings(BlockConstructor.GetTxString("A", "501", "0")
                    )
                ));
            BlockTreeSetup blockTreeSetup = new BlockTreeSetup(blockArray);

            ResultWrapper<UInt256?> resultWrapper = blockTreeSetup.EthRpcModule.eth_gasPrice();
            
            resultWrapper.Data.Should().Be((UInt256?) 500);
        }

        [Test]
        public void Eth_gasPrice_BlockWithMoreThanThreeTxs_OnlyAddsThreeGasPriceTxs()
        {
            Block[] blockArray = BlockConstructor.GetBlocksFromKeyValuePairs(BlockConstructor.BlockNumberAndTxStringsKeyValuePair(0, BlockConstructor.CollectTxStrings(BlockConstructor.GetTxString("A", "4", "0"), BlockConstructor.GetTxString("B", "3", "0"), BlockConstructor.GetTxString("C", "2", "0"), BlockConstructor.GetTxString("D", "1", "0")
                    )
                ));
            BlockTreeSetup blockTreeSetup = new BlockTreeSetup(blockArray);

            blockTreeSetup.EthRpcModule.eth_gasPrice();
            
            List<UInt256> gasPriceList = blockTreeSetup.GasPriceOracle.TxGasPriceList;
            gasPriceList.Count.Should().Be(3);
        }
        
        [Test] 
        public void Eth_gasPrice_BlocksWithMoreThanThreeTxs_OnlyAddsThreeLowestEffectiveGasPriceTxs()
        {
            Block[] blockArray = BlockConstructor.GetBlocksFromKeyValuePairs(BlockConstructor.BlockNumberAndTxStringsKeyValuePair(0, BlockConstructor.CollectTxStrings(BlockConstructor.GetTxString("A", "4", "0"), BlockConstructor.GetTxString("B", "3", "0"), BlockConstructor.GetTxString("C", "2", "0"), BlockConstructor.GetTxString("D", "1", "0")
                    )
                ), BlockConstructor.BlockNumberAndTxStringsKeyValuePair(1, BlockConstructor.CollectTxStrings(BlockConstructor.GetTxString("A", "8", "1"), BlockConstructor.GetTxString("B", "7", "1"), BlockConstructor.GetTxString("C", "6", "1"), BlockConstructor.GetTxString("D", "5", "1")
                    )
                ));
            BlockTreeSetup blockTreeSetup = new BlockTreeSetup(blockArray);
            
            blockTreeSetup.EthRpcModule.eth_gasPrice();
            List<UInt256> gasPriceList = blockTreeSetup.GasPriceOracle.TxGasPriceList;
            
            IEnumerable<UInt256> correctGasPriceList = new List<UInt256> {1,2,3,5,6,7};
            gasPriceList.Should().Equal(correctGasPriceList);
        }
        
        [Test]
        public void Eth_gasPrice_WhenBlocksHaveNoTx_GasPriceShouldBeOne()
        {
            Block[] blocks = BlockConstructor.GetBlocksFromKeyValuePairs(BlockConstructor.BlockNumberAndTxStringsKeyValuePair(0, null), BlockConstructor.BlockNumberAndTxStringsKeyValuePair(1, null), BlockConstructor.BlockNumberAndTxStringsKeyValuePair(2, null), BlockConstructor.BlockNumberAndTxStringsKeyValuePair(3, null), BlockConstructor.BlockNumberAndTxStringsKeyValuePair(4, null));
            BlockTreeSetup blockTreeSetup = new BlockTreeSetup(blocks);
            ResultWrapper<UInt256?> resultWrapper = blockTreeSetup.EthRpcModule.eth_gasPrice();
            
            resultWrapper.Data.Should().Be((UInt256?) 1);
        }
        
        [Test]
        public void Eth_gasPrice_ReturnDefaultGasPrice_EmptyBlocksAtEndEqualToEight()
        {
            Block[] blocks = BlockConstructor.GetBlocksFromKeyValuePairs(BlockConstructor.BlockNumberAndTxStringsKeyValuePair(0, BlockConstructor.CollectTxStrings(BlockConstructor.GetTxString("A", "2", "0"), BlockConstructor.GetTxString("B", "3", "0")
                        )
                ), BlockConstructor.BlockNumberAndTxStringsKeyValuePair(1, null), BlockConstructor.BlockNumberAndTxStringsKeyValuePair(2, null), BlockConstructor.BlockNumberAndTxStringsKeyValuePair(3, null), BlockConstructor.BlockNumberAndTxStringsKeyValuePair(4, null), BlockConstructor.BlockNumberAndTxStringsKeyValuePair(5, null), BlockConstructor.BlockNumberAndTxStringsKeyValuePair(6, null), BlockConstructor.BlockNumberAndTxStringsKeyValuePair(7, null), BlockConstructor.BlockNumberAndTxStringsKeyValuePair(8, null)
                );

            BlockTreeSetup blockTreeSetup = new BlockTreeSetup(blocks);
            ResultWrapper<UInt256?> resultWrapper = blockTreeSetup.EthRpcModule.eth_gasPrice();
            
            resultWrapper.Data.Should().Be((UInt256?) 1); 
        }

        [Test]
        public void Eth_gasPrice_WhenHeadBlockIsNotChanged_ShouldUsePreviouslyCalculatedGasPrice()
        {
            const int normalErrorCode = 0;
            int noHeadBlockChangeErrorCode = GasPriceConfig.NoHeadBlockChangeErrorCode;

            BlockTreeSetup blockTreeSetup = new BlockTreeSetup();
            ResultWrapper<UInt256?> firstResult = blockTreeSetup.EthRpcModule.eth_gasPrice();
            ResultWrapper<UInt256?> secondResult = blockTreeSetup.EthRpcModule.eth_gasPrice();

            firstResult.Data.Should().Be(secondResult.Data);
            firstResult.ErrorCode.Should().Be(normalErrorCode);
            secondResult.ErrorCode.Should().Be(noHeadBlockChangeErrorCode);
        }
        
        [Test]
        public void Eth_gasPrice_TxGasPricesAreBelowThreshold_ReplaceGasPriceUnderThresholdWithDefaultPrice()
        {
            BlockTreeSetup blockTreeSetup = new BlockTreeSetup(ignoreUnder: 4);
            blockTreeSetup.EthRpcModule.eth_gasPrice();
            
            List<UInt256> expected = new List<UInt256> {1,1,4,5,6};
            blockTreeSetup.GasPriceOracle.TxGasPriceList.Should().Equal(expected); 
        }

        [TestCase(false, 5)] //Tx Gas Prices: 1,1,2,3,4,5,6,9,10,11 Index: (10-1) * 3/5 rounds to 5, Gas Price: 5
        [TestCase(true, 5)]  //Tx Gas Prices: 0,0,1,2,3,4,5,6,9,10,11 Index: (11-1) * 3/5 = 6, Gas Price: 5
        public void Eth_gasPrice_InEip1559Mode_ShouldCalculateTxGasPricesDifferently(bool eip1559Enabled, int expected)
        {
            Transaction[] eip1559TxGroup = BlockConstructor.GetTransactionsFromTxStrings(BlockConstructor.CollectTxStrings(BlockConstructor.GetTxString("B","7","2"), BlockConstructor.GetTxString("B","8","3")
                    ), IsEip1559()
                );
            Transaction[] notEip1559TxGroup = BlockConstructor.GetTransactionsFromTxStrings(BlockConstructor.CollectTxStrings(BlockConstructor.GetTxString("B","9","4"), BlockConstructor.GetTxString("B","10","5"), BlockConstructor.GetTxString("B","11","6")
                    ), IsNotEip1559()
                );

            BlockTreeSetup blockTreeSetup = new BlockTreeSetup();
            Block eip1559Block = BlockConstructor.GetBlockWithNumberParentHashAndTxInfo(5, HashOfLastBlockIn(blockTreeSetup), eip1559TxGroup);
            Block nonEip1559Block = BlockConstructor.GetBlockWithNumberParentHashAndTxInfo(6, eip1559Block.Hash, notEip1559TxGroup);
            blockTreeSetup = new BlockTreeSetup(new[]{eip1559Block, nonEip1559Block},true, 
                eip1559Enabled: eip1559Enabled);

            ResultWrapper<UInt256?> resultWrapper = blockTreeSetup.EthRpcModule.eth_gasPrice();
            
            resultWrapper.Data.Should().Be((UInt256?) expected); 
        }

        private bool IsEip1559()
        {
            return true;
        }

        private bool IsNotEip1559()
        {
            return false;
        }

        private Keccak HashOfLastBlockIn(BlockTreeSetup blockTreeSetup)
        {
            return blockTreeSetup.Blocks[^1].Hash;
        }


        [Test]
        public void Eth_gasPrice_TxCountNotGreaterThanLimit_GetTxFromMoreBlocks()
        {
            Block[] blocks = BlockConstructor.GetBlocksFromKeyValuePairs(BlockConstructor.BlockNumberAndTxStringsKeyValuePair(0, BlockConstructor.CollectTxStrings(BlockConstructor.GetTxString("A", "0", "0"), BlockConstructor.GetTxString("B", "1", "0")
                    )
                ), BlockConstructor.BlockNumberAndTxStringsKeyValuePair(1, BlockConstructor.CollectTxStrings(BlockConstructor.GetTxString("C", "2", "0"), BlockConstructor.GetTxString("D", "3","0")
                    )
                ), BlockConstructor.BlockNumberAndTxStringsKeyValuePair(2, BlockConstructor.CollectTxStrings(BlockConstructor.GetTxString("A", "4", "0")
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
            Block[] blocks = BlockConstructor.GetBlocksFromKeyValuePairs(BlockConstructor.BlockNumberAndTxStringsKeyValuePair(0, BlockConstructor.CollectTxStrings(BlockConstructor.GetTxString("A", "3", "0"), BlockConstructor.GetTxString("B", "4", "0")
                    )
                ), BlockConstructor.BlockNumberAndTxStringsKeyValuePair(1, BlockConstructor.CollectTxStrings(BlockConstructor.GetTxString("C", "5", "0"), BlockConstructor.GetTxString("D", "6","0")
                    )
                ), BlockConstructor.BlockNumberAndTxStringsKeyValuePair(2, BlockConstructor.CollectTxStrings(BlockConstructor.GetTxString("A", "7", "0"), BlockConstructor.GetTxString("B", "8", "1")
                    )
                ));


            BlockTreeSetup blockTreeSetup = new BlockTreeSetup(blocks: blocks, blockLimit: 4);
            ResultWrapper<UInt256?> resultWrapper = blockTreeSetup.EthRpcModule.eth_gasPrice();
            
            resultWrapper.Result.Should().Be(Result.Success); 
        }
        
        [Test]
        public void Eth_gasPrice_GetTxFromMinBlocks_NumTxInMinBlocksGreaterThanOrEqualToLimit()
        {
            Block[] blocks = BlockConstructor.GetBlocksFromKeyValuePairs(BlockConstructor.BlockNumberAndTxStringsKeyValuePair(0, BlockConstructor.CollectTxStrings(BlockConstructor.GetTxString("A", "1", "0"), BlockConstructor.GetTxString("B", "2", "0")
                    )
                ), BlockConstructor.BlockNumberAndTxStringsKeyValuePair(1, BlockConstructor.CollectTxStrings(BlockConstructor.GetTxString("C", "3", "0"), BlockConstructor.GetTxString("D", "4", "0")
                    )
                ), BlockConstructor.BlockNumberAndTxStringsKeyValuePair(2, BlockConstructor.CollectTxStrings(BlockConstructor.GetTxString("A", "5","1"), BlockConstructor.GetTxString("B", "6","1")
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
            Address minerAddress = BlockConstructor.PrivateKeyForLetter('A').Address;
            Block block = BlockConstructor.GetBlockWithBeneficiaryBlockNumberAndTxInfo(minerAddress, 0, BlockConstructor.CollectTxStrings(BlockConstructor.GetTxString("A", "7", "0"), BlockConstructor.GetTxString("B", "8", "0"), BlockConstructor.GetTxString("C", "9", "0")
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
                BlockConstructor b = new BlockConstructor();
                Blocks = b.GetBlocksFromKeyValuePairs(
                    BlockConstructor.BlockNumberAndTxStringsKeyValuePair(0, BlockConstructor.CollectTxStrings(
                            BlockConstructor.GetTxString("A", "1", "0"),
                            BlockConstructor.GetTxString("B", "2", "0")
                        )
                    ),
                    BlockConstructor.BlockNumberAndTxStringsKeyValuePair(1, BlockConstructor.CollectTxStrings(
                            BlockConstructor.GetTxString("C", "3", "0")
                        )
                    ),
                    BlockConstructor.BlockNumberAndTxStringsKeyValuePair(2, BlockConstructor.CollectTxStrings(
                            BlockConstructor.GetTxString("D", "5", "0")
                        )
                    ),
                    BlockConstructor.BlockNumberAndTxStringsKeyValuePair(3, BlockConstructor.CollectTxStrings(
                            BlockConstructor.GetTxString("A", "4", "1")
                        )
                    ),
                    BlockConstructor.BlockNumberAndTxStringsKeyValuePair(4, BlockConstructor.CollectTxStrings(
                            BlockConstructor.GetTxString("B", "6", "1")
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
