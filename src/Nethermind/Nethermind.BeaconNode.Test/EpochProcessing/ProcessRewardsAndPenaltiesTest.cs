using System;
using System.Collections.Generic;
using System.Linq;
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
    public class ProcessRewardsAndPenaltiesTest
    {
        [TestMethod]
        public void GenesisEpochNoAttestationsNoPenalties()
        {
            // Arrange
            var testServiceProvider = TestSystem.BuildTestServiceProvider();
            var state = TestState.PrepareTestState(testServiceProvider);

            var initialValues = testServiceProvider.GetService<IOptions<InitialValues>>().Value;
            var beaconChainUtility = testServiceProvider.GetService<BeaconChainUtility>();

            var preState = BeaconState.Clone(state);

            var stateEpoch = beaconChainUtility.ComputeEpochAtSlot(state.Slot);
            stateEpoch.ShouldBe(initialValues.GenesisEpoch);

            // Act
            RunProcessRewardsAndPenalties(testServiceProvider, state);

            // Assert
            for (var index = 0; index < preState.Validators.Count; index++)
            {
                state.Balances[index].ShouldBe(preState.Balances[index], $"Balance {index}");
            }
        }

        [TestMethod]
        public void GenesisEpochFullAttestationsNoRewards()
        {
            // Arrange
            var testServiceProvider = TestSystem.BuildTestServiceProvider();
            var state = TestState.PrepareTestState(testServiceProvider);

            var initialValues = testServiceProvider.GetService<IOptions<InitialValues>>().Value;
            var timeParameters = testServiceProvider.GetService<IOptions<TimeParameters>>().Value;

            var beaconChainUtility = testServiceProvider.GetService<BeaconChainUtility>();

            var attestations = new List<Attestation>();
            for (var slot = Slot.Zero; slot < timeParameters.SlotsPerEpoch - new Slot(1); slot += new Slot(1))
            {
                // create an attestation for each slot
                if (slot < timeParameters.SlotsPerEpoch)
                {
                    var attestation = TestAttestation.GetValidAttestation(testServiceProvider, state, slot, CommitteeIndex.None, signed: true);
                    attestations.Add(attestation);
                }

                // fill each created slot in state after inclusion delay
                if (slot >= timeParameters.MinimumAttestationInclusionDelay)
                {
                    var index = slot - timeParameters.MinimumAttestationInclusionDelay;
                    var includeAttestation = attestations[(int)(ulong)index];
                    TestAttestation.AddAttestationsToState(testServiceProvider, state, new[] { includeAttestation }, state.Slot);
                }

                TestState.NextSlot(testServiceProvider, state);
            }

            // ensure has not cross the epoch boundary
            var stateEpoch = beaconChainUtility.ComputeEpochAtSlot(state.Slot);
            stateEpoch.ShouldBe(initialValues.GenesisEpoch);

            var preState = BeaconState.Clone(state);

            // Act
            RunProcessRewardsAndPenalties(testServiceProvider, state);

            // Assert
            for (var index = 0; index < preState.Validators.Count; index++)
            {
                state.Balances[index].ShouldBe(preState.Balances[index], $"Balance {index}");
            }
        }

        [TestMethod]
        public void FullAttestations()
        {
            // Arrange
            var testServiceProvider = TestSystem.BuildTestServiceProvider();
            var state = TestState.PrepareTestState(testServiceProvider);

            var beaconStateTransition = testServiceProvider.GetService<BeaconStateTransition>();

            var attestations = PrepareStateWithFullAttestations(testServiceProvider, state);

            var preState = BeaconState.Clone(state);

            // Act
            RunProcessRewardsAndPenalties(testServiceProvider, state);

            // Assert
            var pendingAttestations = attestations.Select(x => new PendingAttestation(x.AggregationBits, x.Data, Slot.None, ValidatorIndex.None));
            var attestingIndices = beaconStateTransition.GetUnslashedAttestingIndices(state, pendingAttestations).ToList();

            attestingIndices.Count.ShouldBeGreaterThan(0);

            for (var index = 0; index < preState.Validators.Count; index++)
            {
                var validatorIndex = new ValidatorIndex((ulong)index);
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
            var timeParameters = testServiceProvider.GetService<IOptions<TimeParameters>>().Value;
            var initialValues = testServiceProvider.GetService<IOptions<InitialValues>>().Value;

            var beaconChainUtility = testServiceProvider.GetService<BeaconChainUtility>();

            var attestations = new List<Attestation>();
            var maxSlot = timeParameters.SlotsPerEpoch + timeParameters.MinimumAttestationInclusionDelay;
            for (var slot = Slot.Zero; slot < maxSlot; slot += new Slot(1))
            {
                // create an attestation for each slot in epoch
                if (slot < timeParameters.SlotsPerEpoch)
                {
                    var attestation = TestAttestation.GetValidAttestation(testServiceProvider, state, Slot.None, CommitteeIndex.None, signed: true);
                    attestations.Add(attestation);
                }

                // fill each created slot in state after inclusion delay
                if (slot >= timeParameters.MinimumAttestationInclusionDelay)
                {
                    var index = slot - timeParameters.MinimumAttestationInclusionDelay;
                    var includeAttestation = attestations[(int)(ulong)index];
                    TestAttestation.AddAttestationsToState(testServiceProvider, state, new[] { includeAttestation }, state.Slot);
                }

                TestState.NextSlot(testServiceProvider, state);
            }

            var stateEpoch = beaconChainUtility.ComputeEpochAtSlot(state.Slot);
            stateEpoch.ShouldBe(initialValues.GenesisEpoch + new Epoch(1));

            state.PreviousEpochAttestations.Count.ShouldBe((int)(ulong)timeParameters.SlotsPerEpoch);

            return attestations;
        }

        private void RunProcessRewardsAndPenalties(IServiceProvider testServiceProvider, BeaconState state)
        {
            TestProcessUtility.RunEpochProcessingWith(testServiceProvider, state, TestProcessStep.ProcessRewardsAndPenalties);
        }
    }
}
