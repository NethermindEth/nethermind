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
using Nethermind.Core2.Types;
using Shouldly;
namespace Nethermind.BeaconNode.Test.BlockProcessing
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
