// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nethermind.Core2.Configuration;
using Nethermind.BeaconNode.Test.Helpers;
using Nethermind.Core2.Containers;
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

            ChainConstants chainConstants = testServiceProvider.GetService<ChainConstants>();
            IBeaconChainUtility beaconChainUtility = testServiceProvider.GetService<IBeaconChainUtility>();

            BeaconState preState = BeaconState.Clone(state);

            Epoch stateEpoch = beaconChainUtility.ComputeEpochAtSlot(state.Slot);
            stateEpoch.ShouldBe(chainConstants.GenesisEpoch);

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

            ChainConstants chainConstants = testServiceProvider.GetService<ChainConstants>();
            TimeParameters timeParameters = testServiceProvider.GetService<IOptions<TimeParameters>>().Value;

            IBeaconChainUtility beaconChainUtility = testServiceProvider.GetService<IBeaconChainUtility>();

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
            stateEpoch.ShouldBe(chainConstants.GenesisEpoch);

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
            var pendingAttestations = attestations.Select(x => new PendingAttestation(x.AggregationBits, x.Data, Slot.Zero, ValidatorIndex.Zero));
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
            ChainConstants chainConstants = testServiceProvider.GetService<ChainConstants>();
            TimeParameters timeParameters = testServiceProvider.GetService<IOptions<TimeParameters>>().Value;

            IBeaconChainUtility beaconChainUtility = testServiceProvider.GetService<IBeaconChainUtility>();

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
            stateEpoch.ShouldBe(chainConstants.GenesisEpoch + Epoch.One);

            state.PreviousEpochAttestations.Count.ShouldBe((int)timeParameters.SlotsPerEpoch);

            return attestations;
        }

        private void RunProcessRewardsAndPenalties(IServiceProvider testServiceProvider, BeaconState state)
        {
            TestProcessUtility.RunEpochProcessingWith(testServiceProvider, state, TestProcessStep.ProcessRewardsAndPenalties);
        }
    }
}
