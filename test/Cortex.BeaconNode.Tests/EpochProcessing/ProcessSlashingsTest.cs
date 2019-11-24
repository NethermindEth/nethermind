using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Cortex.BeaconNode.Configuration;
using Cortex.BeaconNode.Tests.Helpers;
using Cortex.Containers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Shouldly;

namespace Cortex.BeaconNode.Tests.EpochProcessing
{
    [TestClass]
    public class ProcessSlashingsTest
    {
        [TestMethod]
        public void MaximumPenalties()
        {
            // Arrange
            TestConfiguration.GetMinimalConfiguration(
                out var chainConstants,
                out var miscellaneousParameterOptions,
                out var gweiValueOptions,
                out var initialValueOptions,
                out var timeParameterOptions,
                out var stateListLengthOptions,
                out var rewardsAndPenaltiesOptions,
                out var maxOperationsPerBlockOptions,
                out _);
            (var beaconChainUtility, var beaconStateAccessor, var beaconStateMutator, var beaconStateTransition, var state) = TestState.PrepareTestState(chainConstants, miscellaneousParameterOptions, gweiValueOptions, initialValueOptions, timeParameterOptions, stateListLengthOptions, rewardsAndPenaltiesOptions, maxOperationsPerBlockOptions);

            var slashedCount = (state.Validators.Count / 3) + 1;
            var currentEpoch = beaconStateAccessor.GetCurrentEpoch(state);
            var outEpoch = currentEpoch + new Epoch((ulong)stateListLengthOptions.CurrentValue.EpochsPerSlashingsVector / 2);

            var slashedIndices = Enumerable.Range(0, slashedCount).ToList();
            SlashValidators(state, slashedIndices, Enumerable.Repeat(outEpoch, slashedCount),
                stateListLengthOptions.CurrentValue,
                beaconStateAccessor, beaconStateMutator);

            var totalBalance = beaconStateAccessor.GetTotalActiveBalance(state);
            var totalPenalties = state.Slashings.Aggregate(Gwei.Zero, (accumulator, x) => accumulator + x);

            (totalBalance / 3).ShouldBeLessThanOrEqualTo(totalPenalties);

            // Act
            RunProcessSlashings(beaconStateTransition, timeParameterOptions.CurrentValue, state);

            // Assert
            foreach (var index in slashedIndices)
            {
                state.Balances[index].ShouldBe(Gwei.Zero, $"Incorrect balance {index}");
            }
        }

        private void SlashValidators(BeaconState state, IEnumerable<int> indices, IEnumerable<Epoch> outEpochs,
            StateListLengths stateListLengths,
            BeaconStateAccessor beaconStateAccessor, BeaconStateMutator beaconStateMutator)
        {
            var totalSlashedBalance = Gwei.Zero;
            var items = indices.Zip(outEpochs, (index, outEpoch) => new { index, outEpoch });
            foreach (var item in items)
            {
                var validator = state.Validators[item.index];
                validator.SetSlashed();
                beaconStateMutator.InitiateValidatorExit(state, new ValidatorIndex((ulong)item.index));
                validator.SetWithdrawableEpoch(item.outEpoch);
                totalSlashedBalance += validator.EffectiveBalance;
            }

            var currentEpoch = beaconStateAccessor.GetCurrentEpoch(state);
            var slashingsIndex = currentEpoch % stateListLengths.EpochsPerSlashingsVector;
            state.SetSlashings(slashingsIndex, totalSlashedBalance);
        }

        private void RunProcessSlashings(BeaconStateTransition beaconStateTransition, TimeParameters timeParameters, BeaconState state)
        {
            TestProcessUtility.RunEpochProcessingWith(beaconStateTransition, timeParameters, state, TestProcessStep.ProcessSlashings);
        }
    }
}
