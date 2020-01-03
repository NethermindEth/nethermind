//  Copyright (c) 2018 Demerzel Solutions Limited
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

using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nethermind.Core2.Configuration;
using Nethermind.BeaconNode.Containers;
using Nethermind.BeaconNode.Test.Helpers;
using Nethermind.Core2.Types;
using Shouldly;

namespace Nethermind.BeaconNode.Test.BlockProcessing
{
    [TestClass]
    public class ProcessDepositTest
    {
        [TestMethod]
        public void NewDepositUnderMax()
        {
            // Arrange
            var testServiceProvider = TestSystem.BuildTestServiceProvider();
            var state = TestState.PrepareTestState(testServiceProvider);

            // fresh deposit = next validator index = validator appended to registry
            var validatorIndex = new ValidatorIndex((ulong)state.Validators.Count);

            // effective balance will be 1 EFFECTIVE_BALANCE_INCREMENT smaller because of this small decrement.
            var gweiValues = testServiceProvider.GetService<IOptions<GweiValues>>().Value;
            var amount = gweiValues.MaximumEffectiveBalance - new Gwei(1);

            var deposit = TestDeposit.PrepareStateAndDeposit(testServiceProvider, state, validatorIndex, amount, Hash32.Zero, signed: true);

            RunDepositProcessing(testServiceProvider, state, deposit, validatorIndex, expectValid: true, effective: true);
        }

        //    Run ``process_deposit``, yielding:
        //  - pre-state('pre')
        //  - deposit('deposit')
        //  - post-state('post').
        //If ``valid == False``, run expecting ``AssertionError``
        private void RunDepositProcessing(IServiceProvider testServiceProvider, BeaconState state, Deposit deposit, ValidatorIndex validatorIndex, bool expectValid, bool effective)
        {
            var gweiValues = testServiceProvider.GetService<IOptions<GweiValues>>().Value;
            var beaconStateTransition = testServiceProvider.GetService<BeaconStateTransition>();

            var preValidatorCount = state.Validators.Count;
            var preBalance = Gwei.Zero;
            if ((int)(ulong)validatorIndex < preValidatorCount)
            {
                preBalance = TestState.GetBalance(state, validatorIndex);
            }

            if (!expectValid)
            {
                Should.Throw<Exception>(() =>
                {
                    beaconStateTransition.ProcessDeposit(state, deposit);
                });
                return;
            }

            beaconStateTransition.ProcessDeposit(state, deposit);

            if (!effective)
            {
                state.Validators.Count.ShouldBe(preValidatorCount);
                state.Balances.Count.ShouldBe(preValidatorCount);
                if ((int)(ulong)validatorIndex < preValidatorCount)
                {
                    var balance = TestState.GetBalance(state, validatorIndex);
                    balance.ShouldBe(preBalance);
                }
            }
            else
            {
                if ((int)(ulong)validatorIndex < preValidatorCount)
                {
                    // top up
                    state.Validators.Count.ShouldBe(preValidatorCount);
                    state.Balances.Count.ShouldBe(preValidatorCount);
                }
                else
                {
                    // new validator
                    state.Validators.Count.ShouldBe(preValidatorCount + 1);
                    state.Balances.Count.ShouldBe(preValidatorCount + 1);
                }

                var balance = TestState.GetBalance(state, validatorIndex);
                var expectedBalance = preBalance + deposit.Data.Amount;
                balance.ShouldBe(expectedBalance);

                var expectedEffectiveBalance = Gwei.Min(gweiValues.MaximumEffectiveBalance, expectedBalance);
                expectedEffectiveBalance -= expectedEffectiveBalance % gweiValues.EffectiveBalanceIncrement;
                state.Validators[(int)(ulong)validatorIndex].EffectiveBalance.ShouldBe(expectedEffectiveBalance);
            }

            state.Eth1DepositIndex.ShouldBe(state.Eth1Data.DepositCount);
        }
    }
}
