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
using System;
using System.Linq;
using FluentAssertions;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Facade;
using Nethermind.Int256;
using Nethermind.JsonRpc.Modules.Eth;
using Nethermind.JsonRpc.Modules.Eth.FeeHistory;
using Nethermind.Logging;
using NSubstitute;
using NUnit.Framework;
using static Nethermind.JsonRpc.Test.Modules.GasPriceOracleTests;
namespace Nethermind.JsonRpc.Test.Modules.Eth
{
    public partial class EthRpcModuleTests
    {
        //Todo do a test about greater than 1024 blocks?
        //Todo a test for if pendingblock less than blockNumber?
        [Test]
        public void GetFeeHistory_NewestBlockIsNull_ReturnsFailingWrapper()
        {
            IBlockFinder blockFinder = Substitute.For<IBlockFinder>();
            blockFinder.FindBlock(Arg.Any<long>()).Returns((Block?) null);
            FeeHistoryOracle feeHistoryOracle = GetSubstitutedFeeHistoryOracle(blockFinder: blockFinder);
            ResultWrapper<FeeHistoryResults> expected = 
                    ResultWrapper<FeeHistoryResults>.Fail("newestBlock: Block is not available", 
                        ErrorCodes.ResourceUnavailable);

            ResultWrapper<FeeHistoryResults> resultWrapper = feeHistoryOracle.GetFeeHistory(1, 
                new BlockParameter((long) 0), null);
            
            resultWrapper.Should().BeEquivalentTo(expected);
        }
        
        
        [TestCase(3,5)]
        [TestCase(4,10)]
        [TestCase(0,1)]
        public void GetFeeHistory_IfPendingBlockDoesNotExistAndLastBlockNumberGreaterThanHeadNumber_ReturnsError(long pendingBlockNumber, long lastBlockNumber)
        {
            IBlockFinder blockFinder = Substitute.For<IBlockFinder>();
            blockFinder.FindPendingBlock().Returns(Build.A.Block.WithNumber(pendingBlockNumber).TestObject);
            FeeHistoryOracle feeHistoryOracle = GetSubstitutedFeeHistoryOracle(blockFinder: blockFinder);
            ResultWrapper<FeeHistoryResults> expected = 
                    ResultWrapper<FeeHistoryResults>.Fail("newestBlock: Block is not available", 
                        ErrorCodes.ResourceUnavailable);
            
            ResultWrapper<FeeHistoryResults> resultWrapper =
                feeHistoryOracle.GetFeeHistory(1, new BlockParameter(lastBlockNumber), null);
    
            resultWrapper.Should().BeEquivalentTo(expected);
        }
        [Test]
        public void GetFeeHistory_BlockCountIsLessThanOne_ReturnsFailingWrapper()
        {
            IBlockFinder blockFinder = Substitute.For<IBlockFinder>();
            blockFinder.FindBlock(Arg.Any<long>()).Returns(Build.A.Block.TestObject);
            FeeHistoryOracle feeHistoryOracle = GetSubstitutedFeeHistoryOracle(blockFinder: blockFinder);
            ResultWrapper<FeeHistoryResults> expected = 
                    ResultWrapper<FeeHistoryResults>.Fail("newestBlock: Block is not available", 
                        ErrorCodes.ResourceUnavailable);

            ResultWrapper<FeeHistoryResults> resultWrapper = feeHistoryOracle.GetFeeHistory(0, 
                new BlockParameter(), null);
            
            resultWrapper.Should().BeEquivalentTo(expected);
        }
        
        [Test]
        public void GetFeeHistory_IfRewardPercentilesNotInAscendingOrder_ResultsInFailure()
        {
            int blockCount = 10;
            double[] rewardPercentiles = {0, 2, 3, 5, 1};
            IBlockFinder blockFinder = Substitute.For<IBlockFinder>();
            blockFinder.FindBlock(Arg.Any<long>()).Returns(Build.A.Block.TestObject);
            FeeHistoryOracle feeHistoryOracle = GetSubstitutedFeeHistoryOracle(blockFinder: blockFinder);
            ResultWrapper<FeeHistoryResults> expected = ResultWrapper<FeeHistoryResults>.Fail($"rewardPercentiles: Some values are below 0 or greater than 100.", 
                                ErrorCodes.InvalidParams);
            
            ResultWrapper<FeeHistoryResults> resultWrapper =
                feeHistoryOracle.GetFeeHistory(blockCount, new BlockParameter(), rewardPercentiles);

            resultWrapper.Result.Should().BeEquivalentTo(expected);
        }
        
        [TestCase(new double[] {-1, 1, 2})]
        [TestCase(new[] {1, 2.2, 101, 102})]
        public void GetFeeHistory_IfRewardPercentilesContainInvalidNumber_ResultsInFailure(double[] rewardPercentiles)
        {
            int blockCount = 10;
            IBlockFinder blockFinder = Substitute.For<IBlockFinder>();
            blockFinder.FindBlock(Arg.Any<long>()).Returns(Build.A.Block.TestObject);
            FeeHistoryOracle feeHistoryOracle = GetSubstitutedFeeHistoryOracle(blockFinder: blockFinder);
            
            ResultWrapper<FeeHistoryResults> resultWrapper = feeHistoryOracle.GetFeeHistory(blockCount, new BlockParameter(), rewardPercentiles);

            resultWrapper.Result.Should().BeEquivalentTo(ResultWrapper<FeeHistoryResults>.Fail($"rewardPercentiles: Some values are below 0 or greater than 100.", 
                                ErrorCodes.InvalidParams));
        }
        
        [TestCase(new double[] {1, 2, 3})]
        [TestCase(new[] {1, 1.5, 2, 66, 67.5, 100})]
        public void GetFeeHistory_IfNewestBlockAndBlockCountAndRewardPercentilesAreValid_ResultIsSuccessful(double[] rewardPercentiles)
        {
            int blockCount = 10;
            IBlockFinder blockFinder = Substitute.For<IBlockFinder>();
            blockFinder.FindBlock(Arg.Any<long>()).Returns(Build.A.Block.TestObject);
            FeeHistoryOracle feeHistoryOracle = GetSubstitutedFeeHistoryOracle(blockFinder: blockFinder);
            
            ResultWrapper<FeeHistoryResults> resultWrapper = feeHistoryOracle.GetFeeHistory(blockCount, new BlockParameter(), rewardPercentiles);

            resultWrapper.Result.Should().BeEquivalentTo(ResultWrapper<FeeHistoryResults>.Success(null));
        }

        [TestCase(5,  6)] //Target gas used: 3/2 = 1.5 | Actual Gas used = 3 | Base Fee Delta = Max((((3-1.5) * 5)/1 / 8, 1) = 1 | Next Base Fee = 5 + 1 = 6 
        [TestCase(11, 12)] //Target gas used: 3/2 = 1.5 | Actual Gas used = 3 | Base Fee Delta = Max((((3-1.5)/1) * 11) / 8, 1) = 1 | Next Base Fee = 11 + 1 = 12 
        [TestCase(20, 22)] //Target gas used: 100/2 = 50 | Actual Gas used = 95 | Base Fee Delta = Max((((95-50)/50) * 20) / 8, 1) = 2 | Next Base Fee = 20 + 2 = 22
        [TestCase(20, 20)] //Target gas used: 100/2 = 50 | Actual Gas used = 40 | Base Fee Delta = (((50-40)/50) * 20) / 8 = 0 | Next Base Fee = 20 - 0 = 20 
        [TestCase(50,  49)] //Target gas used: 100/2 = 50 | Actual Gas used = 40 | Base Fee Delta = (((50-40)/50) * 50) / 8 = 1 | Next Base Fee = 50 - 1 = 49
        public void GetFeeHistory_IfLondonEnabled_NextBaseFeePerGasCalculatedCorrectly(long baseFee, long expectedNextBaseFee)
        {
            int blockCount = 1;
            IBlockFinder blockFinder = Substitute.For<IBlockFinder>();
            BlockHeader blockHeader = Build.A.BlockHeader.WithBaseFee((UInt256) baseFee).TestObject;
            Block headBlock = Build.A.Block.Genesis.WithHeader(blockHeader).TestObject;
            blockFinder.FindBlock(Arg.Is<long>(l => l == 0)).Returns(headBlock);
            ISpecProvider specProvider = GetSpecProviderWithEip1559EnabledAs(true);
            
            FeeHistoryOracle feeHistoryOracle = GetSubstitutedFeeHistoryOracle(blockFinder: blockFinder, specProvider: specProvider);

            ResultWrapper<FeeHistoryResults> resultWrapper = feeHistoryOracle.GetFeeHistory(blockCount, new BlockParameter((long) 0), null);

            resultWrapper.Data.BaseFeePerGas![1].Should().Be((UInt256) expectedNextBaseFee);
            resultWrapper.Data.BaseFeePerGas![0].Should().Be((UInt256) baseFee);
        }

        [TestCase(3, 3, 1)] 
        [TestCase(100, 95,0.95)]  
        [TestCase(12, 3, 0.25)] 
        [TestCase(100, 40,  0.4)]  
        [TestCase(3, 1, 0.33)] 
        public void GetFeeHistory_IfLondonEnabled_GasUsedRatioCalculatedCorrectly(long gasLimit, long gasUsed, double expectedGasUsedRatio)
        {
            IBlockFinder blockFinder = Substitute.For<IBlockFinder>();
            BlockHeader blockHeader = Build.A.BlockHeader.WithGasLimit(gasLimit).WithGasUsed(gasUsed).TestObject;
            Block headBlock = Build.A.Block.Genesis.WithHeader(blockHeader).TestObject;
            blockFinder.FindBlock(Arg.Is<long>(l => l == 0)).Returns(headBlock);
            ISpecProvider specProvider = GetSpecProviderWithEip1559EnabledAs(true);
            FeeHistoryOracle feeHistoryOracle = GetSubstitutedFeeHistoryOracle(blockFinder: blockFinder, specProvider: specProvider);

            ResultWrapper<FeeHistoryResults> resultWrapper = feeHistoryOracle.GetFeeHistory(1, new BlockParameter((long) 0), null);

            resultWrapper.Data.GasUsedRatio![0].Should().Be(expectedGasUsedRatio);
        }
        
        [TestCase(3,3)]
        [TestCase(5,5)]
        public void GetFeeHistory_IfLondonNotEnabled_NextBaseFeeIsParentBaseFee(long baseFee, long expectedNextBaseFee)
        {
            ISpecProvider specProvider = GetSpecProviderWithEip1559EnabledAs(false);
            BlockHeader blockHeader = Build.A.BlockHeader.WithBaseFee((UInt256) baseFee).TestObject;
            Block headBlock = Build.A.Block.Genesis.WithHeader(blockHeader).TestObject;
            IBlockFinder blockFinder = Substitute.For<IBlockFinder>();
            blockFinder.FindBlock(Arg.Is<long>(l => l == 0)).Returns(headBlock);
            FeeHistoryOracle feeHistoryOracle = GetSubstitutedFeeHistoryOracle(blockFinder: blockFinder, specProvider: specProvider);
            
            ResultWrapper<FeeHistoryResults> resultWrapper = feeHistoryOracle.GetFeeHistory(1, new BlockParameter((long) 0), null);
            
            resultWrapper.Data.BaseFeePerGas![1].Should().Be((UInt256) expectedNextBaseFee);
            resultWrapper.Data.BaseFeePerGas![0].Should().Be((UInt256) baseFee);
        }
        
        [TestCase(3, 3, 1)] 
        [TestCase(100, 95,0.95)]  
        [TestCase(12, 3, 0.25)] 
        [TestCase(100, 40,  0.4)]  
        [TestCase(3, 1, 0.33)] 
        public void GetFeeHistory_IfLondonNotEnabled_GasUsedRatioCalculatedCorrectly(long gasLimit, long gasUsed, double expectedGasUsedRatio)
        {
            IBlockFinder blockFinder = Substitute.For<IBlockFinder>();
            BlockHeader blockHeader = Build.A.BlockHeader.WithGasLimit(gasLimit).WithGasUsed(gasUsed).TestObject;
            Block headBlock = Build.A.Block.Genesis.WithHeader(blockHeader).TestObject;
            blockFinder.FindBlock(Arg.Is<long>(l => l == 0)).Returns(headBlock);
            ISpecProvider specProvider = GetSpecProviderWithEip1559EnabledAs(false);
            FeeHistoryOracle feeHistoryOracle = GetSubstitutedFeeHistoryOracle(blockFinder: blockFinder, specProvider: specProvider);

            ResultWrapper<FeeHistoryResults> resultWrapper = feeHistoryOracle.GetFeeHistory(1, new BlockParameter((long) 0), null);

            resultWrapper.Data.GasUsedRatio![0].Should().Be(expectedGasUsedRatio);
        }
        
        [TestCase(null)]
        [TestCase(new double[]{})]
        public void GetFeeHistory_IfRewardPercentilesIsNullOrEmpty_RewardsIsNull(double[]? rewardPercentiles)
        {
            FeeHistoryOracle feeHistoryOracle = GetSubstitutedFeeHistoryOracle();

            ResultWrapper<FeeHistoryResults> resultWrapper = feeHistoryOracle.GetFeeHistory(1, new BlockParameter(), rewardPercentiles);

            resultWrapper.Data.Reward.Should().BeNull();
        }
        
        [TestCase(5)]
        [TestCase(7)]
        public void GetFeeHistory_NoTxsInBlock_ReturnsArrayOfZerosAsBigAsRewardPercentiles(int sizeOfRewardPercentiles)
        {
            IBlockFinder blockFinder = Substitute.For<IBlockFinder>();
            Block noTxBlock = Build.A.Block.TestObject;
            blockFinder.FindBlock(0).Returns(noTxBlock);
            FeeHistoryOracle feeHistoryOracle = GetSubstitutedFeeHistoryOracle();
            double[] rewardPercentiles = Enumerable.Range(1, sizeOfRewardPercentiles).Select(x => (double) x).ToArray();

            ResultWrapper<FeeHistoryResults> resultWrapper = feeHistoryOracle.GetFeeHistory(1, new BlockParameter((long) 0), rewardPercentiles);
            
            resultWrapper.Data.Reward.Should().BeEquivalentTo(Enumerable.Repeat(0, sizeOfRewardPercentiles));
        }
        
        
        [TestCase(5,10,6)]
        [TestCase(23, 50, 28)]
        [TestCase(5, 3, 0)]
        public void GetFeeHistory_GivenValidInputs_FirstBlockNumberCalculatedCorrectly(int blockCount, long newestBlockNumber, long expectedOldestBlockNumber)
        {
            FeeHistoryOracle feeHistoryOracle = GetSubstitutedFeeHistoryOracle();
            
            ResultWrapper<FeeHistoryResults> resultWrapper = feeHistoryOracle.GetFeeHistory(blockCount, new BlockParameter(newestBlockNumber), null);

            resultWrapper.Data.OldestBlock.Should().Be(expectedOldestBlockNumber);
        }

        [TestCase(2,2)]
        [TestCase(7,7)]
        [TestCase(32,32)]
        public void ResolveBlockRange_IfLastBlockIsPendingBlock_LastBlockNumberSetToPendingBlockNumber(long blockNumber, long lastBlockNumberExpected)
        {
            IBlockFinder blockFinder = Substitute.For<IBlockFinder>();
            blockFinder.FindPendingBlock().Returns(Build.A.Block.WithNumber(blockNumber).TestObject);
            FeeHistoryOracle feeHistoryOracle = GetSubstitutedFeeHistoryOracle(blockFinder: blockFinder);

            ResultWrapper<FeeHistoryResults> resultWrapper =
                feeHistoryOracle.GetFeeHistory(1, BlockParameter.Pending, null);
            
            resultWrapper.Data.OldestBlock.Should().Be(lastBlockNumberExpected);
        }
        
        [TestCase(2,2)]
        [TestCase(7,7)]
        [TestCase(32,32)]
        public void ResolveBlockRange_IfLastBlockIsLatestBlock_LastBlockNumberSetToHeadBlockNumber(long blockNumber, long lastBlockNumberExpected)
        {
            IBlockFinder blockFinder = Substitute.For<IBlockFinder>();
            blockFinder.FindHeadBlock().Returns(Build.A.Block.WithNumber(blockNumber).TestObject);
            FeeHistoryOracle feeHistoryOracle = GetSubstitutedFeeHistoryOracle(blockFinder: blockFinder);

            ResultWrapper<FeeHistoryResults> resultWrapper =
                feeHistoryOracle.GetFeeHistory(1, BlockParameter.Latest, null);
            
            resultWrapper.Data.OldestBlock.Should().Be(lastBlockNumberExpected);
        }
        
        private static FeeHistoryOracle GetSubstitutedFeeHistoryOracle(
            IBlockFinder? blockFinder = null, 
            IReceiptStorage? receiptStorage = null,
            ISpecProvider? specProvider = null)
        {
            return new(
                blockFinder ?? Substitute.For<IBlockFinder>(),
                receiptStorage ?? Substitute.For<IReceiptStorage>(),
                specProvider ?? Substitute.For<ISpecProvider>());
        }
    }
}
