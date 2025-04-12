// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test.Producers;

public partial class BlockProducerBaseTests
{
    public static partial class BaseFeeTestScenario
    {
        public partial class ScenarioBuilder
        {
            private Address _feeCollector = null!;

            public ScenarioBuilder WithFeeCollector(Address address)
            {
                _feeCollector = address;
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
                UInt256 balanceBefore = _testRpcBlockchain.ReadOnlyState.GetBalance(_feeCollector);
                await _testRpcBlockchain.AddBlock(transactions);
                UInt256 balanceAfter = _testRpcBlockchain.ReadOnlyState.GetBalance(_feeCollector);
                Assert.That(balanceAfter - balanceBefore, Is.EqualTo(expectedFeeCollected));

                return this;
            }
        }
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public async Task FeeCollector_should_collect_burned_fees_when_eip1559_and_fee_collector_are_set()
    {
        long gasTarget = 3000000;
        BaseFeeTestScenario.ScenarioBuilder scenario = BaseFeeTestScenario.GoesLikeThis()
            .WithEip1559TransitionBlock(6)
            .WithFeeCollector(TestItem.AddressE)
            .CreateTestBlockchain(gasTarget)
            .DeployContract()
            .BlocksBeforeTransitionShouldHaveZeroBaseFee()
            .SendLegacyTransaction(gasTarget / 2, 20.GWei())
            .SendEip1559Transaction(gasTarget / 2, 1.GWei(), 20.GWei())
            .SendLegacyTransaction(gasTarget / 2, 20.GWei())
            .AssertNewBlockFeeCollected(4500000.GWei());
        await scenario.Finish();
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public async Task FeeCollector_should_not_collect_burned_fees_when_eip1559_is_not_set()
    {
        long gasTarget = 3000000;
        BaseFeeTestScenario.ScenarioBuilder scenario = BaseFeeTestScenario.GoesLikeThis()
            .WithFeeCollector(TestItem.AddressE)
            .CreateTestBlockchain(gasTarget)
            .DeployContract()
            .BlocksBeforeTransitionShouldHaveZeroBaseFee()
            .SendLegacyTransaction(gasTarget / 2, 20.GWei())
            .SendEip1559Transaction(gasTarget / 2, 1.GWei(), 20.GWei())
            .SendLegacyTransaction(gasTarget / 2, 20.GWei())
            .AssertNewBlockFeeCollected(0);
        await scenario.Finish();
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public async Task FeeCollector_should_not_collect_burned_fees_when_transaction_is_free()
    {
        long gasTarget = 3000000;
        BaseFeeTestScenario.ScenarioBuilder scenario = BaseFeeTestScenario.GoesLikeThis()
            .WithEip1559TransitionBlock(6)
            .WithFeeCollector(TestItem.AddressE)
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
