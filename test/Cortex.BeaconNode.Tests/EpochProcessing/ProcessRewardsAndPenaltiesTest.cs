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
    public class ProcessRewardsAndPenaltiesTest
    {
        [TestMethod]
        public void GenesisEpochNoAttestationsNoPenalties()
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

            var preState = BeaconState.Clone(state);

            var stateEpoch = beaconChainUtility.ComputeEpochAtSlot(state.Slot);
            stateEpoch.ShouldBe(initialValueOptions.CurrentValue.GenesisEpoch);

            // Act
            RunProcessRewardsAndPenalties(beaconStateTransition, timeParameterOptions.CurrentValue, state);

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

            var miscellaneousParameters = miscellaneousParameterOptions.CurrentValue;
            var timeParameters = timeParameterOptions.CurrentValue;
            var stateListLengths = stateListLengthOptions.CurrentValue;
            var maxOperationsPerBlock = maxOperationsPerBlockOptions.CurrentValue;

            var attestations = new List<Attestation>();
            for (var slot = Slot.Zero; slot < timeParameters.SlotsPerEpoch - new Slot(1); slot += new Slot(1))
            {
                // create an attestation for each slot
                if (slot < timeParameters.SlotsPerEpoch)
                {
                    var attestation = TestAttestation.GetValidAttestation(state, slot, CommitteeIndex.None, signed: true,
                        miscellaneousParameters, timeParameters, stateListLengths, maxOperationsPerBlock,
                        beaconChainUtility, beaconStateAccessor, beaconStateTransition);
                    attestations.Add(attestation);
                }

                // fill each created slot in state after inclusion delay
                if (slot >= timeParameters.MinimumAttestationInclusionDelay)
                {
                    var index = slot - timeParameters.MinimumAttestationInclusionDelay;
                    var includeAttestation = attestations[(int)(ulong)index];
                    TestAttestation.AddAttestationToState(state, includeAttestation, state.Slot,
                        miscellaneousParameters, timeParameters, stateListLengths, maxOperationsPerBlock,
                        beaconChainUtility, beaconStateAccessor, beaconStateTransition);
                }

                TestState.NextSlot(state, beaconStateTransition);
            }

            // ensure has not cross the epoch boundary
            var stateEpoch = beaconChainUtility.ComputeEpochAtSlot(state.Slot);
            stateEpoch.ShouldBe(initialValueOptions.CurrentValue.GenesisEpoch);

            var preState = BeaconState.Clone(state);

            // Act
            RunProcessRewardsAndPenalties(beaconStateTransition, timeParameterOptions.CurrentValue, state);

            // Assert
            for (var index = 0; index < preState.Validators.Count; index++)
            {
                state.Balances[index].ShouldBe(preState.Balances[index], $"Balance {index}");
            }
        }

        private void RunProcessRewardsAndPenalties(BeaconStateTransition beaconStateTransition, TimeParameters timeParameters, BeaconState state)
        {
            TestProcessUtility.RunEpochProcessingWith(beaconStateTransition, timeParameters, state, TestProcessStep.ProcessRewardsAndPenalties);
        }
    }
}
