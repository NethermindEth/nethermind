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

using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.JsonRpc.Modules.Eth;
using NSubstitute;
using NUnit.Framework;
using static Nethermind.JsonRpc.Modules.Eth.EthRpcModule;

namespace Nethermind.JsonRpc.Test.Modules.Eth
{
    public partial class EthRpcModuleTests
    {
        [TestFixture]
        public class FeeHistoryManagerTests
        {
            [Test]
            public void GetFeeHistory_IfBlockCountGreaterThan1024_BlockCountSetTo1024()
            {
                IBlockFinder blockFinder = Substitute.For<IBlockFinder>();
                blockFinder.FindPendingBlock().Returns(Build.A.Block.TestObject);
                TestableFeeHistoryManager feeHistoryManager = new TestableFeeHistoryManager(blockFinder);
                
                feeHistoryManager.GetFeeHistory(1025, 0);

                feeHistoryManager.SetToMaxBlockCountCalled.Should().BeTrue();
            }
            
            [TestCase(new double[]{-1,1,2}, "-1")]
            [TestCase(new double[]{-2.2,1,2,101,102}, "-2.2, 101, 102")]
            public void GetFeeHistory_IfRewardPercentilesContainInvalidNumber_ResultsInFailure(double[] rewardPercentiles, string invalidNums)
            {
                TestableFeeHistoryManager feeHistoryManager = new TestableFeeHistoryManager(Substitute.For<IBlockFinder>());

                ResultWrapper<FeeHistoryResult> resultWrapper = feeHistoryManager.GetFeeHistory(10, 10, rewardPercentiles);

                resultWrapper.Result.Should().BeEquivalentTo(Result.Fail($"rewardPercentiles: Values {invalidNums} are below 0 or greater than 100."));
            }

            [Test]
            public void GetFeeHistory_IfRewardPercentilesNotInAscendingOrder_ResultsInFailure()
            {
                FeeHistoryManager feeHistoryManager = new FeeHistoryManager(Substitute.For<IBlockFinder>());
                double[] rewardPercentiles = {0,2,3,5,1};

                ResultWrapper<FeeHistoryResult> resultWrapper = feeHistoryManager.GetFeeHistory(10, 10, rewardPercentiles);

                string expectedMessage =
                    "rewardPercentiles: Value at index 4: 1 is less than the value at previous index 3: 5.";
                resultWrapper.Result.Should().BeEquivalentTo(Result.Fail(expectedMessage));
            }

            class TestableFeeHistoryManager : FeeHistoryManager
            {
                public TestableFeeHistoryManager(IBlockFinder blockFinder) : base(blockFinder)
                {
                }
                public bool SetToMaxBlockCountCalled { get; private set; } = false;

                protected override int GetMaxBlockCount()
                {
                    SetToMaxBlockCountCalled = true;
                    return (0);
                }

            }
        }
    }
}
