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
using FluentAssertions;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Facade;
using Nethermind.JsonRpc.Modules.Eth;
using Nethermind.JsonRpc.Modules.Eth.FeeHistory;
using Nethermind.Logging;
using NSubstitute;
using NUnit.Framework;
using static Nethermind.JsonRpc.Modules.Eth.EthRpcModule;

namespace Nethermind.JsonRpc.Test.Modules.Eth
{
    public partial class EthRpcModuleTests
    {
        //Todo do a test about greater than 1024 blocks?
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
        
        [Test]
        public void GetFeeHistory_BlockCountIsLessThanOne_ReturnsFailingWrapper()
        {
            FeeHistoryOracle feeHistoryOracle = GetSubstitutedFeeHistoryOracle();
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
            FeeHistoryOracle feeHistoryOracle = GetSubstitutedFeeHistoryOracle();
            ResultWrapper<FeeHistoryResults> expected = ResultWrapper<FeeHistoryResults>.Fail($"rewardPercentiles: Some values are below 0 or greater than 100.", 
                                ErrorCodes.InvalidParams);
            
            ResultWrapper<FeeHistoryResults> resultWrapper =
                feeHistoryOracle.GetFeeHistory(blockCount, new BlockParameter(), rewardPercentiles);

            resultWrapper.Result.Should().BeEquivalentTo(expected);
        }
        
        [TestCase(new double[] {-1, 1, 2}, "-1")]
        [TestCase(new[] {-2.2, 1, 2, 101, 102}, "-2.2, 101, 102")]
        public void GetFeeHistory_IfRewardPercentilesContainInvalidNumber_ResultsInFailure(double[] rewardPercentiles,
            string invalidNums)
        {
            int blockCount = 10;
            FeeHistoryOracle feeHistoryOracle = GetSubstitutedFeeHistoryOracle();
            
            ResultWrapper<FeeHistoryResults> resultWrapper = feeHistoryOracle.GetFeeHistory(blockCount, new BlockParameter(), rewardPercentiles);

            resultWrapper.Result.Should().BeEquivalentTo(ResultWrapper<FeeHistoryResults>.Fail($"rewardPercentiles: Some values are below 0 or greater than 100.", 
                                ErrorCodes.InvalidParams));
        }
        
        public static FeeHistoryOracle GetSubstitutedFeeHistoryOracle(
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
