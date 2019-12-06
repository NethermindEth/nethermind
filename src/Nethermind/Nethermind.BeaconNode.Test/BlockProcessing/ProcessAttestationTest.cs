using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nethermind.BeaconNode.Configuration;
using Nethermind.BeaconNode.Containers;
using Nethermind.BeaconNode.Tests.Helpers;
using Nethermind.Core2.Types;
using Shouldly;

namespace Nethermind.BeaconNode.Tests.BlockProcessing
{
    [TestClass]
    public class ProcessAttestationTest
    {
        [TestMethod]
        public void SuccessAttestation()
        {
            // Arrange
            var testServiceProvider = TestSystem.BuildTestServiceProvider();
            var state = TestState.PrepareTestState(testServiceProvider);

            var attestation = TestAttestation.GetValidAttestation(testServiceProvider, state, Slot.None, CommitteeIndex.None, signed: true);

            var timeParameters = testServiceProvider.GetService<IOptions<TimeParameters>>().Value;
            var slot = state.Slot + timeParameters.MinimumAttestationInclusionDelay;
            state.SetSlot(slot);

            RunAttestationProcessing(testServiceProvider, state, attestation, expectValid: true);
        }

        //Run ``process_attestation``, yielding:
        //  - pre-state('pre')
        //  - attestation('attestation')
        //  - post-state('post').
        //If ``valid == False``, run expecting ``AssertionError``
        private void RunAttestationProcessing(IServiceProvider testServiceProvider, BeaconState state, Attestation attestation, bool expectValid)
        {
            var beaconStateAccessor = testServiceProvider.GetService<BeaconStateAccessor>();
            var beaconStateTransition = testServiceProvider.GetService<BeaconStateTransition>();

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
