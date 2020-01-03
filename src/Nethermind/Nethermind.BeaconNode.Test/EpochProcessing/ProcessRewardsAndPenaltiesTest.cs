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
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nethermind.Core2.Configuration;
using Nethermind.BeaconNode.Containers;
using Nethermind.BeaconNode.Test.Helpers;
using Nethermind.Core2.Types;
using Shouldly;

namespace Nethermind.BeaconNode.Test.EpochProcessing
{
    [TestClass]
    public class ProcessRewardsAndPenaltiesTest
    {
        [TestMethod]
        public void GenesisEpochNoAttestationsNoPenalties()
        {
            // Arrange
            IServiceProvider testServiceProvider = TestSystem.BuildTestServiceProvider();
            BeaconState state = TestState.PrepareTestState(testServiceProvider);

            InitialValues initialValues = testServiceProvider.GetService<IOptions<InitialValues>>().Value;
            BeaconChainUtility beaconChainUtility = testServiceProvider.GetService<BeaconChainUtility>();

            BeaconState preState = BeaconState.Clone(state);

            Epoch stateEpoch = beaconChainUtility.ComputeEpochAtSlot(state.Slot);
            stateEpoch.ShouldBe(initialValues.GenesisEpoch);

            // Act
            RunProcessRewardsAndPenalties(testServiceProvider, state);

            // Assert
            for (int index = 0; index < preState.Validators.Count; index++)
            {
                state.Balances[index].ShouldBe(preState.Balances[index], $"Balance {index}");
            }
        }

        [TestMethod]
        public void GenesisEpochFullAttestationsNoRewards()
        {
            // Arrange
            IServiceProvider testServiceProvider = TestSystem.BuildTestServiceProvider();
            BeaconState state = TestState.PrepareTestState(testServiceProvider);

            InitialValues initialValues = testServiceProvider.GetService<IOptions<InitialValues>>().Value;
            TimeParameters timeParameters = testServiceProvider.GetService<IOptions<TimeParameters>>().Value;

            BeaconChainUtility beaconChainUtility = testServiceProvider.GetService<BeaconChainUtility>();

            var attestations = new List<Attestation>();
            for (Slot slot = Slot.Zero; slot < timeParameters.SlotsPerEpoch - new Slot(1); slot += new Slot(1))
            {
                // create an attestation for each slot
                if (slot < timeParameters.SlotsPerEpoch)
                {
                    Attestation attestation = TestAttestation.GetValidAttestation(testServiceProvider, state, slot, CommitteeIndex.None, signed: true);
                    attestations.Add(attestation);
                }

                // fill each created slot in state after inclusion delay
                if (slot >= timeParameters.MinimumAttestationInclusionDelay)
                {
                    Slot index = slot - timeParameters.MinimumAttestationInclusionDelay;
                    Attestation includeAttestation = attestations[(int)(ulong)index];
                    TestAttestation.AddAttestationsToState(testServiceProvider, state, new[] { includeAttestation }, state.Slot);
                }

                TestState.NextSlot(testServiceProvider, state);
            }

            // ensure has not cross the epoch boundary
            Epoch stateEpoch = beaconChainUtility.ComputeEpochAtSlot(state.Slot);
            stateEpoch.ShouldBe(initialValues.GenesisEpoch);

            BeaconState preState = BeaconState.Clone(state);

            // Act
            RunProcessRewardsAndPenalties(testServiceProvider, state);

            // Assert
            for (int index = 0; index < preState.Validators.Count; index++)
            {
                state.Balances[index].ShouldBe(preState.Balances[index], $"Balance {index}");
            }
        }

        [TestMethod]
        public void FullAttestations()
        {
            // Arrange
            IServiceProvider testServiceProvider = TestSystem.BuildTestServiceProvider();
            BeaconState state = TestState.PrepareTestState(testServiceProvider);

            BeaconStateTransition beaconStateTransition = testServiceProvider.GetService<BeaconStateTransition>();

            var attestations = PrepareStateWithFullAttestations(testServiceProvider, state);

            BeaconState preState = BeaconState.Clone(state);

            // Act
            RunProcessRewardsAndPenalties(testServiceProvider, state);

            // Assert
            var pendingAttestations = attestations.Select(x => new PendingAttestation(x.AggregationBits, x.Data, Slot.None, ValidatorIndex.None));
            var attestingIndices = beaconStateTransition.GetUnslashedAttestingIndices(state, pendingAttestations).ToList();

            attestingIndices.Count.ShouldBeGreaterThan(0);

            for (int index = 0; index < preState.Validators.Count; index++)
            {
                ValidatorIndex validatorIndex = new ValidatorIndex((ulong)index);
                if (attestingIndices.Contains(validatorIndex))
                {
                    state.Balances[index].ShouldBeGreaterThan(preState.Balances[index], $"Attesting balance {index}");
                }
                else
                {
                    state.Balances[index].ShouldBeLessThan(preState.Balances[index], $"Non-attesting balance {index}");
                }
            }
        }

        private static IList<Attestation> PrepareStateWithFullAttestations(IServiceProvider testServiceProvider, BeaconState state)
        {
            TimeParameters timeParameters = testServiceProvider.GetService<IOptions<TimeParameters>>().Value;
            InitialValues initialValues = testServiceProvider.GetService<IOptions<InitialValues>>().Value;

            BeaconChainUtility beaconChainUtility = testServiceProvider.GetService<BeaconChainUtility>();

            var attestations = new List<Attestation>();
            ulong maxSlot = timeParameters.SlotsPerEpoch + timeParameters.MinimumAttestationInclusionDelay;
            for (Slot slot = Slot.Zero; slot < maxSlot; slot += new Slot(1))
            {
                // create an attestation for each slot in epoch
                if (slot < timeParameters.SlotsPerEpoch)
                {
                    Attestation attestation = TestAttestation.GetValidAttestation(testServiceProvider, state, Slot.None, CommitteeIndex.None, signed: true);
                    attestations.Add(attestation);
                }

                // fill each created slot in state after inclusion delay
                if (slot >= timeParameters.MinimumAttestationInclusionDelay)
                {
                    Slot index = slot - timeParameters.MinimumAttestationInclusionDelay;
                    Attestation includeAttestation = attestations[(int)(ulong)index];
                    TestAttestation.AddAttestationsToState(testServiceProvider, state, new[] { includeAttestation }, state.Slot);
                }

                TestState.NextSlot(testServiceProvider, state);
            }

            Epoch stateEpoch = beaconChainUtility.ComputeEpochAtSlot(state.Slot);
            stateEpoch.ShouldBe(initialValues.GenesisEpoch + Epoch.One);

            state.PreviousEpochAttestations.Count.ShouldBe((int)timeParameters.SlotsPerEpoch);

            return attestations;
        }

        private void RunProcessRewardsAndPenalties(IServiceProvider testServiceProvider, BeaconState state)
        {
            TestProcessUtility.RunEpochProcessingWith(testServiceProvider, state, TestProcessStep.ProcessRewardsAndPenalties);
        }
    }
}
