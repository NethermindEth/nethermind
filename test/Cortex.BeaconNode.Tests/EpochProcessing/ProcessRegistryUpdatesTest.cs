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
    public class ProcessRegistryUpdatesTest
    {
        [TestMethod]
        public void BasicActivation()
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
                out var maxOperationsPerBlockOptions);
            (var beaconChainUtility, var beaconStateAccessor, var beaconStateTransition, var state) = TestState.PrepareTestState(chainConstants, miscellaneousParameterOptions, gweiValueOptions, initialValueOptions, timeParameterOptions, stateListLengthOptions, rewardsAndPenaltiesOptions, maxOperationsPerBlockOptions);

            var index = 0;

            MockDeposit(state, index, 
                chainConstants, gweiValueOptions.CurrentValue,
                beaconChainUtility, beaconStateAccessor);

            for (var count = (ulong)0; count < (ulong)timeParameterOptions.CurrentValue.MaximumSeedLookahead + 1; count++)
            {
                TestState.NextEpoch(state, timeParameterOptions.CurrentValue, beaconStateTransition);
            }

            // Act
            RunProcessRegistryUpdates(beaconStateTransition, timeParameterOptions.CurrentValue, state);

            // Assert
            var validator = state.Validators[index];
            validator.ActivationEligibilityEpoch.ShouldNotBe(chainConstants.FarFutureEpoch);
            validator.ActivationEpoch.ShouldNotBe(chainConstants.FarFutureEpoch);
            var currentEpoch = beaconStateAccessor.GetCurrentEpoch(state);
            var isActive = beaconChainUtility.IsActiveValidator(validator, currentEpoch);
            isActive.ShouldBeTrue();
        }

        private void MockDeposit(BeaconState state, int index,
            ChainConstants chainConstants, GweiValues gweiValues,
            BeaconChainUtility beaconChainUtility, BeaconStateAccessor beaconStateAccessor)
        {
            var validator = state.Validators[index];
            var currentEpoch = beaconStateAccessor.GetCurrentEpoch(state);
            var isActive = beaconChainUtility.IsActiveValidator(validator, currentEpoch);
            isActive.ShouldBeTrue();

            validator.SetEligible(chainConstants.FarFutureEpoch);
            validator.SetActive(chainConstants.FarFutureEpoch);
            validator.SetEffectiveBalance(gweiValues.MaximumEffectiveBalance);

            var isActiveAfter = beaconChainUtility.IsActiveValidator(validator, currentEpoch);
            isActiveAfter.ShouldBeFalse();
        }

        private void RunProcessRegistryUpdates(BeaconStateTransition beaconStateTransition, TimeParameters timeParameters, BeaconState state)
        {
            TestProcessUtility.RunEpochProcessingWith(beaconStateTransition, timeParameters, state, TestProcessStep.ProcessRegistryUpdates);
        }
    }
}
