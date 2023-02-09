// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nethermind.Core2.Configuration;
using Nethermind.BeaconNode.Test.Helpers;
using Nethermind.Core2.Containers;
using Nethermind.Core2.Crypto;
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
            IServiceProvider testServiceProvider = TestSystem.BuildTestServiceProvider();
            BeaconState state = TestState.PrepareTestState(testServiceProvider);

            // fresh deposit = next validator index = validator appended to registry
            ValidatorIndex validatorIndex = new ValidatorIndex((ulong)state.Validators.Count);

            // effective balance will be 1 EFFECTIVE_BALANCE_INCREMENT smaller because of this small decrement.
            GweiValues gweiValues = testServiceProvider.GetService<IOptions<GweiValues>>().Value;
            Gwei amount = gweiValues.MaximumEffectiveBalance - new Gwei(1);

            Deposit deposit = TestDeposit.PrepareStateAndDeposit(testServiceProvider, state, validatorIndex, amount, Bytes32.Zero, signed: true);

            RunDepositProcessing(testServiceProvider, state, deposit, validatorIndex, expectValid: true, effective: true);
        }

        //    Run ``process_deposit``, yielding:
        //  - pre-state('pre')
        //  - deposit('deposit')
        //  - post-state('post').
        //If ``valid == False``, run expecting ``AssertionError``
        private void RunDepositProcessing(IServiceProvider testServiceProvider, BeaconState state, Deposit deposit, ValidatorIndex validatorIndex, bool expectValid, bool effective)
        {
            GweiValues gweiValues = testServiceProvider.GetService<IOptions<GweiValues>>().Value;
            BeaconStateTransition beaconStateTransition = testServiceProvider.GetService<BeaconStateTransition>();

            int preValidatorCount = state.Validators.Count;
            Gwei preBalance = Gwei.Zero;
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
                    Gwei balance = TestState.GetBalance(state, validatorIndex);
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

                Gwei balance = TestState.GetBalance(state, validatorIndex);
                Gwei expectedBalance = preBalance + deposit.Data.Item.Amount;
                balance.ShouldBe(expectedBalance);

                Gwei expectedEffectiveBalance = Gwei.Min(gweiValues.MaximumEffectiveBalance, expectedBalance);
                expectedEffectiveBalance -= expectedEffectiveBalance % gweiValues.EffectiveBalanceIncrement;
                state.Validators[(int)(ulong)validatorIndex].EffectiveBalance.ShouldBe(expectedEffectiveBalance);
            }

            state.Eth1DepositIndex.ShouldBe(state.Eth1Data.DepositCount);
        }
    }
}
