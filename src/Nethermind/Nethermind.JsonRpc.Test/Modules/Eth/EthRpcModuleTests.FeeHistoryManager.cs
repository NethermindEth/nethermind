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
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.JsonRpc.Modules.Eth;
using Nethermind.Logging;
using NSubstitute;
using NUnit.Framework;
using static Nethermind.JsonRpc.Modules.Eth.EthRpcModule;

namespace Nethermind.JsonRpc.Test.Modules.Eth
{
    public partial class EthRpcModuleTests
    {
        [TestCase(1025)]
        [TestCase(1024)]
        public void GetFeeHistory_IfBlockCountGreaterThan1024_BlockCountSetTo1024(long blockCount)
        {
            IBlockFinder blockFinder = Substitute.For<IBlockFinder>();
            ILogger logger = Substitute.For<ILogger>();
            blockFinder.FindPendingBlock().Returns(Build.A.Block.WithNumber(1).TestObject);
            IBlockRangeManager blockRangeManager = Substitute.For<IBlockRangeManager>();
            blockRangeManager.ResolveBlockRange(ref Arg.Any<long>(), ref Arg.Any<long>(), Arg.Any<int>(), ref Arg.Any<long?>())
                .Returns(ResultWrapper<BlockRangeInfo>.Success(new BlockRangeInfo()));
            FeeHistoryManager feeHistoryManager = new(blockFinder, logger, blockRangeManager);
            
            feeHistoryManager.GetFeeHistory(ref blockCount, 0);

            blockCount.Should().Be(1024);
        }
        
        [TestCase(new double[]{-1,1,2}, "-1")]
        [TestCase(new[]{-2.2,1,2,101,102}, "-2.2, 101, 102")]
        public void GetFeeHistory_IfRewardPercentilesContainInvalidNumber_ResultsInFailure(double[] rewardPercentiles, string invalidNums)
        {
            long blockCount = 10;
            IBlockFinder blockFinder = Substitute.For<IBlockFinder>();
            ILogger logger = Substitute.For<ILogger>();
            FeeHistoryManager feeHistoryManager = new(blockFinder, logger);

            ResultWrapper<FeeHistoryResult> resultWrapper = feeHistoryManager.GetFeeHistory(ref blockCount, 10, rewardPercentiles);

            resultWrapper.Result.Should().BeEquivalentTo(Result.Fail($"rewardPercentiles: Values {invalidNums} are below 0 or greater than 100."));
        }

        [Test]
        public void GetFeeHistory_IfRewardPercentilesNotInAscendingOrder_ResultsInFailure()
        {
            long blockCount = 10;
            IBlockFinder blockFinder = Substitute.For<IBlockFinder>();
            ILogger logger = Substitute.For<ILogger>();
            FeeHistoryManager feeHistoryManager = new FeeHistoryManager(blockFinder, logger);
            double[] rewardPercentiles = {0,2,3,5,1};

            ResultWrapper<FeeHistoryResult> resultWrapper = feeHistoryManager.GetFeeHistory(ref blockCount, 10, rewardPercentiles);

            string expectedMessage =
                "rewardPercentiles: Value at index 4: 1 is less than the value at previous index 3: 5.";
            resultWrapper.Result.Should().BeEquivalentTo(Result.Fail(expectedMessage));
        }

        [TestCase(5,10,6)]
        [TestCase(23, 50, 28)]
        [TestCase(5, 3, 0)]
        public void FeeHistoryLookup_GivenValidInputs_FirstBlockNumberCalculatedCorrectly(long blockCount, long lastBlockNumber, long expectedFirstBlockNumber)
        {
            IBlockFinder blockFinder = Substitute.For<IBlockFinder>();
            ILogger logger = Substitute.For<ILogger>();
            IBlockRangeManager blockRangeManager = Substitute.For<IBlockRangeManager>();
            TestableFeeHistoryManager testableFeeHistoryManager = new(blockFinder, logger, blockRangeManager);

            testableFeeHistoryManager.FeeHistoryLookup(blockCount, lastBlockNumber);

            testableFeeHistoryManager.BlockFeeInfos![0].BlockNumber.Should().Be(expectedFirstBlockNumber);
        }
        
        
        [TestCase(5,10,new long[]{6,7,8,9,10})]
        [TestCase(1, 5, new long[]{5})]
        [TestCase(5, 3, new long[]{0,1,2,3})]
        public void FeeHistoryLookup_GivenValidInputs_NumbersInBlockFeeInfoListCalculatedCorrectly(long blockCount, long lastBlockNumber, long[] expectedNumbers)
        {
            IBlockFinder blockFinder = Substitute.For<IBlockFinder>();
            ILogger logger = Substitute.For<ILogger>();
            IBlockRangeManager blockRangeManager = Substitute.For<IBlockRangeManager>();
            TestableFeeHistoryManager testableFeeHistoryManager = new(blockFinder, logger, blockRangeManager);

            testableFeeHistoryManager.FeeHistoryLookup(blockCount, lastBlockNumber);

            long[] numbersInList = testableFeeHistoryManager.BlockFeeInfos!.Select(b => b.BlockNumber).ToArray();
            numbersInList.Should().BeEquivalentTo(expectedNumbers);
        }

        [TestCase(3,4,3)]
        [TestCase(5,10,5)]
        [TestCase(3,2,2)]
        public void GetBlockFeeInfo_IfPendingBlockNumberLessThanBlockNumber_SetBlockNumberToPendingBlockNumber(long pendingBlockNumber, long argBlockNumber, long expected)
        {
            IBlockFinder blockFinder = Substitute.For<IBlockFinder>();
            ILogger logger = Substitute.For<ILogger>();
            IBlockRangeManager blockRangeManager = Substitute.For<IBlockRangeManager>();
            FeeHistoryManager feeHistoryManager = new FeeHistoryManager(blockFinder, logger, blockRangeManager);
            Block pendingBlock = (Build.A.Block.WithNumber(pendingBlockNumber).TestObject);
            
            BlockFeeInfo result = feeHistoryManager.GetBlockFeeInfo(argBlockNumber, null, pendingBlock);

            result.BlockNumber.Should().Be(expected);
        }
        
        [TestCase(null,4)]
        [TestCase(null, 10)]
        [TestCase( new double[]{},4)]
        [TestCase(new double[]{}, 10)]
        public void GetBlockFeeInfo_IfPendingBlockNumberIsValidAndRewardPercentilesIsNullOrEmpty_GetCorrespondingHeaderFromBlockFinder(double[] rewardPercentiles, long argBlockNumber)
        {
            IBlockFinder blockFinder = Substitute.For<IBlockFinder>();
            Block correspondingBlock = Build.A.Block.WithNumber(argBlockNumber).TestObject;
            BlockHeader correspondingHeader = Build.A.BlockHeader.WithNumber(argBlockNumber).TestObject;
            blockFinder.FindBlock(Arg.Is<long>(n => n == argBlockNumber))
                .Returns(correspondingBlock);
            blockFinder.FindHeader(Arg.Is<long>(n => n == argBlockNumber))
                .Returns(correspondingHeader);
            ILogger logger = Substitute.For<ILogger>();
            IBlockRangeManager blockRangeManager = Substitute.For<IBlockRangeManager>();
            FeeHistoryManager feeHistoryManager = new(blockFinder, logger, blockRangeManager);
            
            BlockFeeInfo result = feeHistoryManager.GetBlockFeeInfo(argBlockNumber, rewardPercentiles, null);

            result.BlockHeader.Should().BeEquivalentTo(correspondingHeader);
            result.Block.Should().BeNull();
        }
        
        [TestCase(new double[]{10}, 4)]
        [TestCase(new double[]{10,20}, 10)]
        [TestCase(new double[]{0,100}, 2)]
        public void GetBlockFeeInfo_IfBlockNumberIsValidAndRewardPercentilesIsNotNullOrEmpty_GetCorrespondingBlockAndHeaderFromBlockFinder(double[] rewardPercentiles, long argBlockNumber)
        {
            IBlockFinder blockFinder = Substitute.For<IBlockFinder>();
            Block correspondingBlock = Build.A.Block.WithNumber(argBlockNumber).TestObject;
            BlockHeader correspondingHeader = Build.A.BlockHeader.WithNumber(argBlockNumber).TestObject;
            blockFinder.FindBlock(Arg.Is<long>(n => n == argBlockNumber))
                .Returns(correspondingBlock);
            blockFinder.FindHeader(Arg.Is<long>(n => n == argBlockNumber))
                .Returns(correspondingHeader);
            ILogger logger = Substitute.For<ILogger>();
            IBlockRangeManager blockRangeManager = Substitute.For<IBlockRangeManager>();
            FeeHistoryManager feeHistoryManager = new(blockFinder, logger, blockRangeManager);
            
            BlockFeeInfo result = feeHistoryManager.GetBlockFeeInfo(argBlockNumber, rewardPercentiles, null);

            result.BlockHeader.Should().BeEquivalentTo(correspondingHeader);
            result.Block.Should().BeEquivalentTo(correspondingBlock);
        }
        
        [Test]
        public void CreateFeeHistoryResult_IfBlockFeeInfosHas0Elements_ShouldThrowException()
        {
            IBlockFinder blockFinder = Substitute.For<IBlockFinder>();
            ILogger logger = Substitute.For<ILogger>();
            IBlockRangeManager blockRangeManager = Substitute.For<IBlockRangeManager>();
            FeeHistoryManager feeHistoryManager = new(blockFinder, logger, blockRangeManager);
            
            Action action = () => feeHistoryManager.CreateFeeHistoryResult(new List<BlockFeeInfo>(), 0);

            action.Should().Throw<ArgumentException>().WithMessage("`blockFeeInfos` has 0 elements.");
        }
        
        [Test]
        public void CreateFeeHistoryResult_IfBlockFeeInfosLengthNotEqualToBlockCount_ShouldThrowException()
        {
            IBlockFinder blockFinder = Substitute.For<IBlockFinder>();
            ILogger logger = Substitute.For<ILogger>();
            IBlockRangeManager blockRangeManager = Substitute.For<IBlockRangeManager>();
            FeeHistoryManager feeHistoryManager = new(blockFinder, logger, blockRangeManager);
            BlockFeeInfo blockFeeInfo = new();
            
            Action action = () => feeHistoryManager.CreateFeeHistoryResult(new List<BlockFeeInfo>{blockFeeInfo}, 2);

            action.Should().Throw<ArgumentException>().WithMessage("`blockCount`: 2 was not equal to number of blocks' information in `blockFeeInfos`: 1.");
        }
        
        [Test]
        public void CreateFeeHistoryResult_IfBlockCountEqualTo0_ShouldThrowException()
        {
            IBlockFinder blockFinder = Substitute.For<IBlockFinder>();
            ILogger logger = Substitute.For<ILogger>();
            IBlockRangeManager blockRangeManager = Substitute.For<IBlockRangeManager>();
            FeeHistoryManager feeHistoryManager = new(blockFinder, logger, blockRangeManager);
            BlockFeeInfo blockFeeInfo = new();
            
            Action action = () => feeHistoryManager.CreateFeeHistoryResult(new List<BlockFeeInfo>{blockFeeInfo}, 0);

            action.Should().Throw<ArgumentException>().WithMessage("`blockCount` is equal to 0.");
        }

        [Test]
        public void CreateFeeHistoryResult_GivenValidArguments_ShouldReturnProperFeeHistoryResult()
        {
            List<BlockFeeInfo> blockFeeInfos = new()
            {
                new(){Reward = new UInt256[] {1, 4, 9}, BaseFee = 2, NextBaseFee = 3, GasUsedRatio = 4},
                new(){Reward = new UInt256[] {16, 25}, BaseFee = 3, NextBaseFee = 3, GasUsedRatio = 8},
                new(){Reward = new UInt256[] {36}, BaseFee = 4, NextBaseFee = 6, GasUsedRatio = 12},
                new(){Reward = new UInt256[] {}, BaseFee = 5, NextBaseFee = 6, GasUsedRatio = 15}
            };
            
            UInt256[][] expectedRewards = 
            {
                new UInt256[]{1,4,9},
                new UInt256[]{16,25},
                new UInt256[]{36},
                new UInt256[]{}
            };
            UInt256[] expectedBaseFees = {2,3,4,5,6};
            float[] expectedGasUsedRatio = {4,8,12,15};
            FeeHistoryResult expected = new() {_reward = expectedRewards, _baseFee = expectedBaseFees, _gasUsedRatio = expectedGasUsedRatio};

            IBlockTree blockTree = Substitute.For<IBlockTree>();
            ILogger logger = Substitute.For<ILogger>();
            IBlockRangeManager blockRangeManager = Substitute.For<IBlockRangeManager>(); 
            FeeHistoryManager feeHistoryManager = new FeeHistoryManager(blockTree, logger, blockRangeManager);
            
            FeeHistoryResult result = feeHistoryManager.CreateFeeHistoryResult(blockFeeInfos, 4);

            result.Should().BeEquivalentTo(expected);
        }

        class TestableFeeHistoryManager : FeeHistoryManager
        {
            public List<BlockFeeInfo>? BlockFeeInfos { get; private set; }

            public TestableFeeHistoryManager(
                IBlockFinder blockFinder,
                ILogger logger,
                IBlockRangeManager? blockRangeManager = null,
                bool londonEnabled = true) : 
                base(blockFinder,
                    logger,
                    blockRangeManager)
            {
                BlockFeeInfos = null;
            }

            protected override ResultWrapper<FeeHistoryResult> SuccessfulResult(long blockCount, List<BlockFeeInfo> blockFeeInfos)
            {
                BlockFeeInfos = blockFeeInfos;
                return ResultWrapper<FeeHistoryResult>.Success(new FeeHistoryResult());
            }

            protected internal override BlockFeeInfo GetBlockFeeInfo(long blockNumber, double[]? rewardPercentiles,
                Block? pendingBlock)
            {
                BlockFeeInfo blockFeeInfo = new() {BlockNumber = blockNumber};
                return blockFeeInfo;
            }

        }
    }
}
