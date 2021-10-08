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
using Nethermind.BeaconNode.Test.Helpers;
using Nethermind.Core2.Containers;
using Nethermind.Core2.Crypto;
using Nethermind.Core2.Types;
using Shouldly;
namespace Nethermind.BeaconNode.Test.EpochProcessing
{
    [TestClass]
    public class ProcessRegistryUpdatesTest
    {
        [TestMethod]
        public void BasicActivation()
        {
            // Arrange
            IServiceProvider testServiceProvider = TestSystem.BuildTestServiceProvider();
            BeaconState state = TestState.PrepareTestState(testServiceProvider);

            ChainConstants chainConstants = testServiceProvider.GetService<ChainConstants>();
            TimeParameters timeParameters = testServiceProvider.GetService<IOptions<TimeParameters>>().Value;

            IBeaconChainUtility beaconChainUtility = testServiceProvider.GetService<IBeaconChainUtility>();
            BeaconStateAccessor beaconStateAccessor = testServiceProvider.GetService<BeaconStateAccessor>();

            int index = 0;

            MockDeposit(testServiceProvider, state, index);

            for (ulong count = (ulong)0; count < (ulong)timeParameters.MaximumSeedLookahead + 1; count++)
            {
                TestState.NextEpoch(testServiceProvider, state);
            }
            
            // eligibility must be finalized
            Epoch currentEpoch = beaconStateAccessor.GetCurrentEpoch(state);
            state.SetFinalizedCheckpoint(new Checkpoint(currentEpoch +new Epoch(2), Root.Zero));

            // Act
            RunProcessRegistryUpdates(testServiceProvider, state);

            // Assert
            Validator validator = state.Validators[index];
            validator.ActivationEligibilityEpoch.ShouldNotBe(chainConstants.FarFutureEpoch);
            validator.ActivationEpoch.ShouldNotBe(chainConstants.FarFutureEpoch); 
            bool isActive = beaconChainUtility.IsActiveValidator(validator, currentEpoch + timeParameters.MaximumSeedLookahead + Epoch.One);
            isActive.ShouldBeTrue();
        }

        [TestMethod]
        public void ActivationQueueSorting()
        {
            // Arrange
            IServiceProvider testServiceProvider = TestSystem.BuildTestServiceProvider();
            BeaconState state = TestState.PrepareTestState(testServiceProvider);

            ChainConstants chainConstants = testServiceProvider.GetService<ChainConstants>();
            BeaconStateAccessor beaconStateAccessor = testServiceProvider.GetService<BeaconStateAccessor>();

            int mockActivations = 10;
            Epoch currentEpoch = beaconStateAccessor.GetCurrentEpoch(state);
            for (int index = 0; index < mockActivations; index++)
            {
                MockDeposit(testServiceProvider, state, index);
                state.Validators[index].SetEligible(currentEpoch + Epoch.One);
            }

            // eligibility must be finalized
            state.SetFinalizedCheckpoint(new Checkpoint(currentEpoch +new Epoch(2), Root.Zero));

            // give the last priority over the others
            state.Validators[mockActivations - 1].SetEligible(currentEpoch);

            // make sure we are hitting the churn
            int churnLimit = (int)beaconStateAccessor.GetValidatorChurnLimit(state);
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
            IServiceProvider testServiceProvider = TestSystem.BuildTestServiceProvider();
            BeaconState state = TestState.PrepareTestState(testServiceProvider);

            ChainConstants chainConstants = testServiceProvider.GetService<ChainConstants>();
            GweiValues gweiValues = testServiceProvider.GetService<IOptions<GweiValues>>().Value;
            TimeParameters timeParameters = testServiceProvider.GetService<IOptions<TimeParameters>>().Value;

            IBeaconChainUtility beaconChainUtility = testServiceProvider.GetService<IBeaconChainUtility>();
            BeaconStateAccessor beaconStateAccessor = testServiceProvider.GetService<BeaconStateAccessor>();

            int index = 0;

            Epoch currentEpoch = beaconStateAccessor.GetCurrentEpoch(state);
            Validator validator = state.Validators[index];
            bool isActive = beaconChainUtility.IsActiveValidator(validator, currentEpoch);
            isActive.ShouldBeTrue();
            validator.ExitEpoch.ShouldBe(chainConstants.FarFutureEpoch);

            // Mock an ejection
            state.Validators[index].SetEffectiveBalance(gweiValues.EjectionBalance);

            for (ulong count = (ulong)0; count < (ulong)timeParameters.MaximumSeedLookahead + 1; count++)
            {
                TestState.NextEpoch(testServiceProvider, state);
            }

            // Act
            RunProcessRegistryUpdates(testServiceProvider, state);

            // Assert
            Epoch epochAfter = beaconStateAccessor.GetCurrentEpoch(state);
            Validator validatorAfter = state.Validators[index];
            bool isActiveAfter = beaconChainUtility.IsActiveValidator(validatorAfter, epochAfter);
            isActiveAfter.ShouldBeFalse();
            validatorAfter.ExitEpoch.ShouldNotBe(chainConstants.FarFutureEpoch);
        }

        private void MockDeposit(IServiceProvider testServiceProvider, BeaconState state, int index)
        {
            ChainConstants chainConstants = testServiceProvider.GetService<ChainConstants>();
            GweiValues gweiValues = testServiceProvider.GetService<IOptions<GweiValues>>().Value;

            IBeaconChainUtility beaconChainUtility = testServiceProvider.GetService<IBeaconChainUtility>();
            BeaconStateAccessor beaconStateAccessor = testServiceProvider.GetService<BeaconStateAccessor>();

            Validator validator = state.Validators[index];
            Epoch currentEpoch = beaconStateAccessor.GetCurrentEpoch(state);
            bool isActive = beaconChainUtility.IsActiveValidator(validator, currentEpoch);
            isActive.ShouldBeTrue();

            validator.SetEligible(chainConstants.FarFutureEpoch);
            validator.SetActive(chainConstants.FarFutureEpoch);
            validator.SetEffectiveBalance(gweiValues.MaximumEffectiveBalance);

            bool isActiveAfter = beaconChainUtility.IsActiveValidator(validator, currentEpoch);
            isActiveAfter.ShouldBeFalse();
        }

        private void RunProcessRegistryUpdates(IServiceProvider testServiceProvider, BeaconState state)
        {
            TestProcessUtility.RunEpochProcessingWith(testServiceProvider, state, TestProcessStep.ProcessRegistryUpdates);
        }
    }
}
