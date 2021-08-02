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

#nullable enable
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.JsonRpc.Modules.Eth;
using Nethermind.JsonRpc.Modules.Eth.GasPrice;
using NSubstitute;
using NSubstitute.Extensions;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Modules
{
    [TestFixture]
    partial class GasPriceOracleTests
    {
        [Test]
        public void GasPriceEstimate_NoChangeInHeadBlock_ReturnsPreviousGasPrice()
        {
            GasPriceOracle testableGasPriceOracle = GetReturnsSameGasPriceGasPriceOracle(lastGasPrice: 7);
            IBlockFinder blockFinder = Substitute.For<IBlockFinder>();
            Block testBlock = Build.A.Block.Genesis.TestObject;
            
            ResultWrapper<UInt256?> resultWrapper = testableGasPriceOracle.GetGasPriceEstimate(testBlock, blockFinder);
            
            resultWrapper.Data.Should().Be((UInt256?) 7);
        }

        private GasPriceOracle GetReturnsSameGasPriceGasPriceOracle(
            ISpecProvider? specProvider = null, 
            UInt256? ignoreUnder = null, 
            int? blockLimit = null, 
            ITxInsertionManager? txInsertionManager = null,
            UInt256? lastGasPrice = null)
        {
            GasPriceOracle gasPriceOracle = GetTestableGasPriceOracle(specProvider, ignoreUnder, blockLimit,
                txInsertionManager, lastGasPrice);
            if (lastGasPrice != null)
            {
                gasPriceOracle.Configure().GetLastGasPrice().Returns(lastGasPrice);
            }
            gasPriceOracle.Configure().ShouldReturnSameGasPrice(Arg.Any<Block?>(), Arg.Any<Block?>(), Arg.Any<UInt256?>())
                .Returns(true);
            return gasPriceOracle;
        }
        
        [Test]
        public void GasPriceEstimate_IfPreviousGasPriceDoesNotExist_FallbackGasPriceSetToDefaultGasPrice()
        {
            GasPriceOracle testableGasPriceOracle = GetTestableGasPriceOracle();
            IBlockFinder blockFinder = Substitute.For<IBlockFinder>();
            Block testBlock = Build.A.Block.Genesis.TestObject;
            
            testableGasPriceOracle.GetGasPriceEstimate(testBlock, blockFinder);
            
            testableGasPriceOracle.FallbackGasPrice.Should().BeEquivalentTo((UInt256?) EthGasPriceConstants.DefaultGasPrice);
        }

        [TestCase(3)]
        [TestCase(10)]
        public void GasPriceEstimate_IfPreviousGasPriceExists_FallbackGasPriceIsSetToPreviousGasPrice(int lastGasPrice)
        {
            GasPriceOracle testableGasPriceOracle = GetTestableGasPriceOracle(lastGasPrice: (UInt256) lastGasPrice);
            IBlockFinder blockFinder = Substitute.For<IBlockFinder>();
            Block testBlock = Build.A.Block.Genesis.TestObject;
            
            testableGasPriceOracle.GetGasPriceEstimate(testBlock, blockFinder);
            
            testableGasPriceOracle.FallbackGasPrice.Should().BeEquivalentTo((UInt256?) lastGasPrice);
        }

        [TestCase(new[]{1,3,5,7,8,9}, 7)] //Last index: 6 - 1 = 5, 60th percentile: 5 * 3/5 = 3, Value: 7
        [TestCase(new[]{0,0,7,9,10,27,83,101}, 10)] //Last index: 8 - 1 = 7, 60th percentile: 7 * 3/5 rounds to 4, Value: 10
        public void GasPriceEstimate_BlockCountEqualToBlocksToCheck_SixtiethPercentileOfMaxIndexReturned(int[] gasPrice, int expected)
        {
            List<UInt256> listOfGasPrices = gasPrice.Select(n => (UInt256) n).ToList();
            GasPriceOracle testableGasPriceOracle = GetTestableGasPriceOracle(sortedTxList: listOfGasPrices);
            IBlockFinder blockFinder = Substitute.For<IBlockFinder>();
            Block testBlock = Build.A.Block.Genesis.TestObject;

            ResultWrapper<UInt256?> resultWrapper = testableGasPriceOracle.GetGasPriceEstimate(testBlock, blockFinder);
            
            resultWrapper.Data.Should().BeEquivalentTo((UInt256?) expected);
        }

        [Test]
        public void GasPriceEstimate_IfCalculatedGasPriceGreaterThanMax_MaxGasPriceReturned()
        {
            
            List<UInt256> listOfGasPrices = new()
            {
                501.GWei()
            }; 
            GasPriceOracle testableGasPriceOracle = GetTestableGasPriceOracle(sortedTxList: listOfGasPrices);
            IBlockFinder blockFinder = Substitute.For<IBlockFinder>();
            Block testBlock = Build.A.Block.Genesis.TestObject;

            ResultWrapper<UInt256?> resultWrapper = testableGasPriceOracle.GetGasPriceEstimate(testBlock, blockFinder);
            
            resultWrapper.Result.Should().Be(Result.Success);
            resultWrapper.Data.Should().BeEquivalentTo((UInt256?) EthGasPriceConstants.MaxGasPrice);
        }
        
        [Test]
        public void GasPriceEstimate_IfEightBlocksWithTwoTransactions_CheckEightBlocks()
        {
            ITxInsertionManager txInsertionManager = Substitute.For<ITxInsertionManager>();
            txInsertionManager.AddValidTxFromBlockAndReturnCount(Arg.Any<Block>()).Returns(2);
            GasPriceOracle testableGasPriceOracle = GetTestableGasPriceOracle(txInsertionManager: txInsertionManager, blockLimit: 8);
            Block headBlock = Build.A.Block.WithNumber(8).TestObject;
            IBlockFinder blockFinder = Substitute.For<IBlockFinder>();
            
            testableGasPriceOracle.GetGasPriceEstimate(headBlock, blockFinder);
            
            txInsertionManager.Received(8).AddValidTxFromBlockAndReturnCount(Arg.Any<Block>());
        }

        [Test]
        public void GasPriceEstimate_IfLastFiveBlocksWithThreeTxAndFirstFourWithOne_CheckSixBlocks()
        {
            ITxInsertionManager txInsertionManager = Substitute.For<ITxInsertionManager>();
            GasPriceOracle testableGasPriceOracle = GetTestableGasPriceOracle(txInsertionManager: txInsertionManager, blockLimit: 8);
            SetUpTxInsertionManagerForSpecificReturns(txInsertionManager);
            Block headBlock = Build.A.Block.WithNumber(8).TestObject;
            IBlockFinder blockFinder = BlockFinderForNineEmptyBlocks();
            
            testableGasPriceOracle.GetGasPriceEstimate(headBlock, blockFinder);
            
            txInsertionManager.Received(8).AddValidTxFromBlockAndReturnCount(Arg.Any<Block>());

            static IBlockFinder BlockFinderForNineEmptyBlocks()
            {
                IBlockFinder blockFinder = Substitute.For<IBlockFinder>();
                Block[] blocks = {
                    Build.A.Block.Genesis.TestObject,
                    Build.A.Block.WithNumber(1).TestObject,
                    Build.A.Block.WithNumber(2).TestObject,
                    Build.A.Block.WithNumber(3).TestObject,
                    Build.A.Block.WithNumber(4).TestObject,
                    Build.A.Block.WithNumber(5).TestObject,
                    Build.A.Block.WithNumber(6).TestObject,
                    Build.A.Block.WithNumber(7).TestObject,
                    Build.A.Block.WithNumber(8).TestObject,
                };
            
                blockFinder.FindBlock(0).Returns(blocks[0]);
                blockFinder.FindBlock(1).Returns(blocks[1]);
                blockFinder.FindBlock(2).Returns(blocks[2]);
                blockFinder.FindBlock(3).Returns(blocks[3]);
                blockFinder.FindBlock(4).Returns(blocks[4]);
                blockFinder.FindBlock(5).Returns(blocks[5]);
                blockFinder.FindBlock(6).Returns(blocks[6]);
                blockFinder.FindBlock(7).Returns(blocks[7]);
                blockFinder.FindBlock(8).Returns(blocks[8]);
            
                return blockFinder;
            }
        }
        private static void SetUpTxInsertionManagerForSpecificReturns(ITxInsertionManager txInsertionManager)
        {
            txInsertionManager.AddValidTxFromBlockAndReturnCount(Arg.Is<Block>(b => b.Number >= 4)).Returns(3);
            txInsertionManager.AddValidTxFromBlockAndReturnCount(Arg.Is<Block>(b => b.Number < 4)).Returns(1);
        }
        
        [Test]
        public void ShouldReturnSameGasPrice_IfLastHeadAndCurrentHeadAreSame_WillReturnTrue()
        {
            Block testBlock = Build.A.Block.Genesis.TestObject;
            GasPriceOracle testableGasPriceOracle = GetTestableGasPriceOracle();
            bool result = testableGasPriceOracle.ShouldReturnSameGasPrice(testBlock, testBlock, 10);

            result.Should().BeTrue();
        }

        [Test]
        public void ShouldReturnSameGasPrice_IfLastHeadAndCurrentHeadAreNotSame_WillReturnFalse()
        {
            Block testBlock = Build.A.Block.Genesis.TestObject;
            Block differentTestBlock = Build.A.Block.WithNumber(1).TestObject;
            GasPriceOracle testableGasPriceOracle = GetTestableGasPriceOracle();
            
            bool result = testableGasPriceOracle.ShouldReturnSameGasPrice(testBlock, differentTestBlock, 10);

            result.Should().BeFalse();
        }

        [Test]
        public void ShouldReturnSameGasPrice_IfLastHeadIsNull_WillReturnFalse()
        {
            Block testBlock = Build.A.Block.Genesis.TestObject;
            GasPriceOracle testableGasPriceOracle = GetTestableGasPriceOracle();
            
            bool result = testableGasPriceOracle.ShouldReturnSameGasPrice(null, testBlock, 10);

            result.Should().BeFalse();
        }
        
        [Test]
        public void ShouldReturnSameGasPrice_IfLastGasPriceIsNull_WillReturnFalse()
        {
            Block testBlock = Build.A.Block.Genesis.TestObject;
            GasPriceOracle testableGasPriceOracle = GetTestableGasPriceOracle();
            
            bool result = testableGasPriceOracle.ShouldReturnSameGasPrice(testBlock, testBlock, null);

            result.Should().BeFalse();
        }

        private GasPriceOracle GetTestableGasPriceOracle(
            ISpecProvider? specProvider = null, 
            UInt256? ignoreUnder = null, 
            int? blockLimit = null, 
            ITxInsertionManager? txInsertionManager = null,
            UInt256? lastGasPrice = null,
            List<UInt256>? sortedTxList = null)
        {
            GasPriceOracle gasPriceOracle = Substitute.ForPartsOf<GasPriceOracle>(
            specProvider ?? Substitute.For<ISpecProvider>(),
            txInsertionManager ?? Substitute.For<ITxInsertionManager>());
            
            if (lastGasPrice != null)
            {
                gasPriceOracle.Configure().GetLastGasPrice().Returns(lastGasPrice);
            }
            
            if (sortedTxList != null)
            {
                gasPriceOracle.Configure().GetSortedTxGasPrices(Arg.Any<Block?>(), Arg.Any<IBlockFinder>()).Returns(sortedTxList);
            }

            if (ignoreUnder != null)
            {
                gasPriceOracle.Configure().GetIgnoreUnder().Returns((UInt256) ignoreUnder);
            }

            if (blockLimit != null)
            {
                gasPriceOracle.Configure().GetBlockLimit().Returns((int) blockLimit);
            }

            return gasPriceOracle;
        }
    }
}
