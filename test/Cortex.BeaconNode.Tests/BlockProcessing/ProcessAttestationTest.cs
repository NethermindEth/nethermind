using System;
using Cortex.BeaconNode.Tests.Helpers;
using Cortex.Containers;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Shouldly;

namespace Cortex.BeaconNode.Tests.BlockProcessing
{
    [TestClass]
    public class ProcessAttestationTest
    {
        [TestMethod]
        public void SuccessAttestation()
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

            var attestation = TestAttestation.GetValidAttestation(state, Slot.None, CommitteeIndex.None, signed: true,
                miscellaneousParameterOptions.CurrentValue, timeParameterOptions.CurrentValue, stateListLengthOptions.CurrentValue, maxOperationsPerBlockOptions.CurrentValue,
                beaconChainUtility, beaconStateAccessor, beaconStateTransition);

            var slot = state.Slot + timeParameterOptions.CurrentValue.MinimumAttestationInclusionDelay;
            state.SetSlot(slot);

            RunAttestationProcessing(state, attestation, expectValid: true,
                beaconStateAccessor, beaconStateTransition);
        }

        //Run ``process_attestation``, yielding:
        //  - pre-state('pre')
        //  - attestation('attestation')
        //  - post-state('post').
        //If ``valid == False``, run expecting ``AssertionError``
        private void RunAttestationProcessing(BeaconState state, Attestation attestation, bool expectValid,
            BeaconStateAccessor beaconStateAccessor, BeaconStateTransition beaconStateTransition)
        {
            if (!expectValid)
            {
                Should.Throw<Exception>(() =>
                {
                    beaconStateTransition.ProcessAttestation(state, attestation);
                });
            }

            var currentEpochCount = state.CurrentEpochAttestations.Count;
            var previousEpochCount = state.PreviousEpochAttestations.Count;

            // process attestation
            beaconStateTransition.ProcessAttestation(state, attestation);

            // Make sure the attestation has been processed
            var currentEpoch = beaconStateAccessor.GetCurrentEpoch(state);
            if (attestation.Data.Target.Epoch == currentEpoch)
            {
                state.CurrentEpochAttestations.Count.ShouldBe(currentEpochCount + 1);
            }
            else
            {
                state.PreviousEpochAttestations.Count.ShouldBe(previousEpochCount + 1);
            }
        }
    }
}
