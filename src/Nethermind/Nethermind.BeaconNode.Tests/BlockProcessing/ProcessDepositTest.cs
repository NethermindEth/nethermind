﻿using System;
using Cortex.BeaconNode.Configuration;
using Cortex.BeaconNode.Tests.Helpers;
using Cortex.Containers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Shouldly;

namespace Cortex.BeaconNode.Tests.BlockProcessing
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
