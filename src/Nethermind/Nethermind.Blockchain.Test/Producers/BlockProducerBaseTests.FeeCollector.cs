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
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test.Producers
{
    public partial class BlockProducerBaseTests
    {
        public static partial class BaseFeeTestScenario
        {
            public partial class ScenarioBuilder
            {
                private Address _eip1559FeeCollector;

                public ScenarioBuilder WithEip1559FeeCollector(Address address)
                {
                    _eip1559FeeCollector = address;
                    return this;
                }
                
                public ScenarioBuilder AssertNewBlockFeeCollected(UInt256 expectedFeeCollected, params Transaction[] transactions)
                {
                    _antecedent = AssertNewBlockFeeCollectedAsync(expectedFeeCollected, transactions);
                    return this;
                }
                
                private async Task<ScenarioBuilder> AssertNewBlockFeeCollectedAsync(UInt256 expectedFeeCollected, params Transaction[] transactions)
                {
                    await ExecuteAntecedentIfNeeded();
                    if (_eip1559FeeCollector is null)
                    {
                        Assert.Fail($"{nameof(IReleaseSpec.Eip1559FeeCollector)} not set");
                    }
                    UInt256 balanceBefore = _testRpcBlockchain.State.GetBalance(_eip1559FeeCollector);
                    await _testRpcBlockchain.AddBlock(transactions);
                    UInt256 balanceAfter = _testRpcBlockchain.State.GetBalance(_eip1559FeeCollector);
                    Assert.AreEqual(expectedFeeCollected, balanceAfter - balanceBefore);

                    return this;
                }
            }
        }
        
        [Test]
        public async Task FeeCollector_should_collect_burned_fees_when_eip1559_and_fee_collector_are_set()
        {
            long gasTarget = 3000000;
            BaseFeeTestScenario.ScenarioBuilder scenario = BaseFeeTestScenario.GoesLikeThis()
                .WithEip1559TransitionBlock(6)
                .WithEip1559FeeCollector(TestItem.AddressE)
                .CreateTestBlockchain(gasTarget)
                .DeployContract()
                .BlocksBeforeTransitionShouldHaveZeroBaseFee()
                .SendLegacyTransaction(gasTarget / 2, 20.GWei())
                .SendEip1559Transaction(gasTarget / 2, 1.GWei(), 20.GWei())
                .SendLegacyTransaction(gasTarget / 2, 20.GWei())
                .AssertNewBlockFeeCollected(4500000.GWei());
            await scenario.Finish();
        }
        
        [Test]
        public async Task FeeCollector_should_not_collect_burned_fees_when_eip1559_is_not_set()
        {
            long gasTarget = 3000000;
            BaseFeeTestScenario.ScenarioBuilder scenario = BaseFeeTestScenario.GoesLikeThis()
                .WithEip1559FeeCollector(TestItem.AddressE)
                .CreateTestBlockchain(gasTarget)
                .DeployContract()
                .BlocksBeforeTransitionShouldHaveZeroBaseFee()
                .SendLegacyTransaction(gasTarget / 2, 20.GWei())
                .SendEip1559Transaction(gasTarget / 2, 1.GWei(), 20.GWei())
                .SendLegacyTransaction(gasTarget / 2, 20.GWei())
                .AssertNewBlockFeeCollected(0);
            await scenario.Finish();
        }
        
        [Test]
        public async Task FeeCollector_should_not_collect_burned_fees_when_transaction_is_free()
        {
            long gasTarget = 3000000;
            BaseFeeTestScenario.ScenarioBuilder scenario = BaseFeeTestScenario.GoesLikeThis()
                .WithEip1559TransitionBlock(6)
                .WithEip1559FeeCollector(TestItem.AddressE)
                .CreateTestBlockchain(gasTarget)
                .DeployContract()
                .BlocksBeforeTransitionShouldHaveZeroBaseFee()
                .SendLegacyTransaction(gasTarget / 2, 20.GWei(), true)
                .SendEip1559Transaction(gasTarget / 2, 1.GWei(), 20.GWei(), true)
                .SendLegacyTransaction(gasTarget / 2, 20.GWei(), true)
                .AssertNewBlockFeeCollected(0);
            await scenario.Finish();
        }
    }
}
