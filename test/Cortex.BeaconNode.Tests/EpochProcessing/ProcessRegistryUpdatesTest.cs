using Cortex.BeaconNode.Configuration;
using Cortex.BeaconNode.Tests.Helpers;
using Cortex.Containers;
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
                out var maxOperationsPerBlockOptions,
                out _);
            (var beaconChainUtility, var beaconStateAccessor, var _, var beaconStateTransition, var state) = TestState.PrepareTestState(chainConstants, miscellaneousParameterOptions, gweiValueOptions, initialValueOptions, timeParameterOptions, stateListLengthOptions, rewardsAndPenaltiesOptions, maxOperationsPerBlockOptions);

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

        [TestMethod]
        public void ActivationQueueSorting()
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
            (var beaconChainUtility, var beaconStateAccessor, var _, var beaconStateTransition, var state) = TestState.PrepareTestState(chainConstants, miscellaneousParameterOptions, gweiValueOptions, initialValueOptions, timeParameterOptions, stateListLengthOptions, rewardsAndPenaltiesOptions, maxOperationsPerBlockOptions);

            var mockActivations = 10;
            var currentEpoch = beaconStateAccessor.GetCurrentEpoch(state);
            for (var index = 0; index < mockActivations; index++)
            {
                MockDeposit(state, index,
                    chainConstants, gweiValueOptions.CurrentValue,
                    beaconChainUtility, beaconStateAccessor);
                state.Validators[index].SetEligible(currentEpoch + new Epoch(1));
            }

            // give the last priority over the others
            state.Validators[mockActivations - 1].SetEligible(currentEpoch);

            // make sure we are hitting the churn
            var churnLimit = (int)beaconStateAccessor.GetValidatorChurnLimit(state);
            mockActivations.ShouldBeGreaterThan(churnLimit);

            // Act
            RunProcessRegistryUpdates(beaconStateTransition, timeParameterOptions.CurrentValue, state);

            // Assert

            //# the first got in as second
            state.Validators[0].ActivationEpoch.ShouldNotBe(chainConstants.FarFutureEpoch);
            //# the prioritized got in as first
            state.Validators[mockActivations - 1].ActivationEpoch.ShouldNotBe(chainConstants.FarFutureEpoch);
            //# the second last is at the end of the queue, and did not make the churn,
            //#  hence is not assigned an activation_epoch yet.
            state.Validators[mockActivations - 2].ActivationEpoch.ShouldBe(chainConstants.FarFutureEpoch);
            //# the one at churn_limit - 1 did not make it, it was out-prioritized
            state.Validators[churnLimit - 1].ActivationEpoch.ShouldBe(chainConstants.FarFutureEpoch);
            //# but the the one in front of the above did
            state.Validators[churnLimit - 2].ActivationEpoch.ShouldNotBe(chainConstants.FarFutureEpoch);
        }

        [TestMethod]
        public void Ejection()
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
            (var beaconChainUtility, var beaconStateAccessor, var _, var beaconStateTransition, var state) = TestState.PrepareTestState(chainConstants, miscellaneousParameterOptions, gweiValueOptions, initialValueOptions, timeParameterOptions, stateListLengthOptions, rewardsAndPenaltiesOptions, maxOperationsPerBlockOptions);

            var index = 0;

            var currentEpoch = beaconStateAccessor.GetCurrentEpoch(state);
            var validator = state.Validators[index];
            var isActive = beaconChainUtility.IsActiveValidator(validator, currentEpoch);
            isActive.ShouldBeTrue();
            validator.ExitEpoch.ShouldBe(chainConstants.FarFutureEpoch);

            // Mock an ejection
            state.Validators[index].SetEffectiveBalance(gweiValueOptions.CurrentValue.EjectionBalance);

            for (var count = (ulong)0; count < (ulong)timeParameterOptions.CurrentValue.MaximumSeedLookahead + 1; count++)
            {
                TestState.NextEpoch(state, timeParameterOptions.CurrentValue, beaconStateTransition);
            }

            // Act
            RunProcessRegistryUpdates(beaconStateTransition, timeParameterOptions.CurrentValue, state);

            // Assert
            var epochAfter = beaconStateAccessor.GetCurrentEpoch(state);
            var validatorAfter = state.Validators[index];
            var isActiveAfter = beaconChainUtility.IsActiveValidator(validatorAfter, epochAfter);
            isActiveAfter.ShouldBeFalse();
            validatorAfter.ExitEpoch.ShouldNotBe(chainConstants.FarFutureEpoch);
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
