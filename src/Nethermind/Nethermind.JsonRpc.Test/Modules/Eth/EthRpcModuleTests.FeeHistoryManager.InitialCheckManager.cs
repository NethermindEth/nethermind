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

using FluentAssertions;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.JsonRpc.Modules.Eth;
using Nethermind.Logging;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Modules.Eth
{
    public partial class EthRpcModuleTests
    {

        [TestCase(1025)]
        [TestCase(1024)]
        public void GetFeeHistory_IfBlockCountGreaterThan1024_BlockCountSetTo1024(long blockCount)
        {
            InitialCheckManager initialCheckManager = new();
            
            initialCheckManager.InitialChecksPassed(ref blockCount, null);

            blockCount.Should().Be(1024);
        }
        
        [TestCase(new double[] {-1, 1, 2}, "-1")]
        [TestCase(new[] {-2.2, 1, 2, 101, 102}, "-2.2, 101, 102")]
        public void GetFeeHistory_IfRewardPercentilesContainInvalidNumber_ResultsInFailure(double[] rewardPercentiles,
            string invalidNums)
        {
            long blockCount = 10;
            InitialCheckManager initialCheckManager = new();
            
            ResultWrapper<FeeHistoryResults> resultWrapper = initialCheckManager.InitialChecksPassed(ref blockCount, rewardPercentiles);

            resultWrapper.Result.Should().BeEquivalentTo(
                Result.Fail($"rewardPercentiles: Values {invalidNums} are below 0 or greater than 100."));
        }

        [Test]
        public void GetFeeHistory_IfRewardPercentilesNotInAscendingOrder_ResultsInFailure()
        {
            long blockCount = 10;
            double[] rewardPercentiles = {0, 2, 3, 5, 1};
            InitialCheckManager initialCheckManager = new();
            
            ResultWrapper<FeeHistoryResults> resultWrapper =
                initialCheckManager.InitialChecksPassed(ref blockCount, rewardPercentiles);

            string expectedMessage =
                "rewardPercentiles: Value at index 4: 1 is less than the value at previous index 3: 5.";
            resultWrapper.Result.Should().BeEquivalentTo(Result.Fail(expectedMessage));
        }
    }
}
