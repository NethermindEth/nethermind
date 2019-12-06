using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nethermind.BeaconNode.Configuration;
using Nethermind.BeaconNode.Containers;
using Nethermind.BeaconNode.Tests.Helpers;
using Shouldly;

namespace Nethermind.BeaconNode.Tests.EpochProcessing
{
    [TestClass]
    public class ProcessRegistryUpdatesTest
    {
        [TestMethod]
        public void BasicActivation()
        {
            // Arrange
            var testServiceProvider = TestSystem.BuildTestServiceProvider();
            var state = TestState.PrepareTestState(testServiceProvider);

            var chainConstants = testServiceProvider.GetService<ChainConstants>();
            var timeParameters = testServiceProvider.GetService<IOptions<TimeParameters>>().Value;

            var beaconChainUtility = testServiceProvider.GetService<BeaconChainUtility>();
            var beaconStateAccessor = testServiceProvider.GetService<BeaconStateAccessor>();

            var index = 0;

            MockDeposit(testServiceProvider, state, index);

            for (var count = (ulong)0; count < (ulong)timeParameters.MaximumSeedLookahead + 1; count++)
            {
                TestState.NextEpoch(testServiceProvider, state);
            }

            // Act
            RunProcessRegistryUpdates(testServiceProvider, state);

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
            var testServiceProvider = TestSystem.BuildTestServiceProvider();
            var state = TestState.PrepareTestState(testServiceProvider);

            var chainConstants = testServiceProvider.GetService<ChainConstants>();
            var beaconStateAccessor = testServiceProvider.GetService<BeaconStateAccessor>();

            var mockActivations = 10;
            var currentEpoch = beaconStateAccessor.GetCurrentEpoch(state);
            for (var index = 0; index < mockActivations; index++)
            {
                MockDeposit(testServiceProvider, state, index);
                state.Validators[index].SetEligible(currentEpoch + new Epoch(1));
            }

            // give the last priority over the others
            state.Validators[mockActivations - 1].SetEligible(currentEpoch);

            // make sure we are hitting the churn
            var churnLimit = (int)beaconStateAccessor.GetValidatorChurnLimit(state);
            mockActivations.ShouldBeGreaterThan(churnLimit);

            // Act
            RunProcessRegistryUpdates(testServiceProvider, state);

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
            var testServiceProvider = TestSystem.BuildTestServiceProvider();
            var state = TestState.PrepareTestState(testServiceProvider);

            var chainConstants = testServiceProvider.GetService<ChainConstants>();
            var gweiValues = testServiceProvider.GetService<IOptions<GweiValues>>().Value;
            var timeParameters = testServiceProvider.GetService<IOptions<TimeParameters>>().Value;

            var beaconChainUtility = testServiceProvider.GetService<BeaconChainUtility>();
            var beaconStateAccessor = testServiceProvider.GetService<BeaconStateAccessor>();

            var index = 0;

            var currentEpoch = beaconStateAccessor.GetCurrentEpoch(state);
            var validator = state.Validators[index];
            var isActive = beaconChainUtility.IsActiveValidator(validator, currentEpoch);
            isActive.ShouldBeTrue();
            validator.ExitEpoch.ShouldBe(chainConstants.FarFutureEpoch);

            // Mock an ejection
            state.Validators[index].SetEffectiveBalance(gweiValues.EjectionBalance);

            for (var count = (ulong)0; count < (ulong)timeParameters.MaximumSeedLookahead + 1; count++)
            {
                TestState.NextEpoch(testServiceProvider, state);
            }

            // Act
            RunProcessRegistryUpdates(testServiceProvider, state);

            // Assert
            var epochAfter = beaconStateAccessor.GetCurrentEpoch(state);
            var validatorAfter = state.Validators[index];
            var isActiveAfter = beaconChainUtility.IsActiveValidator(validatorAfter, epochAfter);
            isActiveAfter.ShouldBeFalse();
            validatorAfter.ExitEpoch.ShouldNotBe(chainConstants.FarFutureEpoch);
        }

        private void MockDeposit(IServiceProvider testServiceProvider, BeaconState state, int index)
        {
            var chainConstants = testServiceProvider.GetService<ChainConstants>();
            var gweiValues = testServiceProvider.GetService<IOptions<GweiValues>>().Value;

            var beaconChainUtility = testServiceProvider.GetService<BeaconChainUtility>();
            var beaconStateAccessor = testServiceProvider.GetService<BeaconStateAccessor>();

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

        private void RunProcessRegistryUpdates(IServiceProvider testServiceProvider, BeaconState state)
        {
            TestProcessUtility.RunEpochProcessingWith(testServiceProvider, state, TestProcessStep.ProcessRegistryUpdates);
        }
    }
}
