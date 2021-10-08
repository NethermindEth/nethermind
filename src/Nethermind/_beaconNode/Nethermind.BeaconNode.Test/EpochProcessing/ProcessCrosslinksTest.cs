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

//using System;
//using System.Collections;
//using System.Linq;
//using Cortex.BeaconNode.Configuration;
//using Cortex.BeaconNode.Tests.Helpers;
//using Cortex.Containers;
//using Microsoft.Extensions.Logging;
//using Microsoft.Extensions.Logging.Console;
//using Microsoft.Extensions.Options;
//using Microsoft.VisualStudio.TestTools.UnitTesting;
//using Shouldly;

//namespace Cortex.BeaconNode.Tests.EpochProcessing
//{
//    [TestClass]
//    public class ProcessCrosslinksTest
//    {
//        [TestMethod]
//        public void NoAttestations()
//        {
//            // Arrange
//            TestConfiguration.GetMinimalConfiguration(
//                out var chainConstants,
//                out var miscellaneousParameterOptions,
//                out var gweiValueOptions,
//                out var initialValueOptions,
//                out var timeParameterOptions,
//                out var stateListLengthOptions,
//                out var maxOperationsPerBlockOptions);
//            (_, _, var beaconStateTransition, var state) = TestState.PrepareTestState(chainConstants, miscellaneousParameterOptions, gweiValueOptions, initialValueOptions, timeParameterOptions, stateListLengthOptions, maxOperationsPerBlockOptions);
//            //// Skip ahead to just before epoch
//            //var nextEpoch = new Epoch(1);
//            //var slot = new Slot((ulong)timeParameterOptions.CurrentValue.SlotsPerEpoch * (ulong)nextEpoch - 1);
//            //state.SetSlot(slot);

//            // Act
//            RunProcessCrosslinks(beaconStateTransition, timeParameterOptions.CurrentValue, state);

//            // Assert
//            for (var shard = 0; shard < (int)(ulong)miscellaneousParameterOptions.CurrentValue.ShardCount; shard++)
//            {
//                state.PreviousCrosslinks[shard].ShouldBe(state.CurrentCrosslinks[shard], $"Shard {shard}");
//            }
//        }

//        [TestMethod]
//        public void SingleCrosslinkUpdateFromCurrentEpoch()
//        {
//            // Arrange
//            TestConfiguration.GetMinimalConfiguration(
//                out var chainConstants,
//                out var miscellaneousParameterOptions,
//                out var gweiValueOptions,
//                out var initialValueOptions,
//                out var timeParameterOptions,
//                out var stateListLengthOptions,
//                out var maxOperationsPerBlockOptions);
//            (var beaconChainUtility, var beaconStateAccessor, var beaconStateTransition, var state) = TestState.PrepareTestState(chainConstants, miscellaneousParameterOptions, gweiValueOptions, initialValueOptions, timeParameterOptions, stateListLengthOptions, maxOperationsPerBlockOptions);

//            TestState.NextEpoch(state, beaconStateTransition, timeParameterOptions.CurrentValue);
//            var attestation = TestAttestation.GetValidAttestation(state, state.Slot, signed: true,
//                miscellaneousParameterOptions.CurrentValue, timeParameterOptions.CurrentValue, stateListLengthOptions.CurrentValue, maxOperationsPerBlockOptions.CurrentValue,
//                beaconChainUtility, beaconStateAccessor);
//            //TestAttestation.FillAggregateAttestation(state, attestation);
//            TestAttestation.AddAttestationToState(state, attestation, state.Slot + timeParameterOptions.CurrentValue.MinimumAttestationInclusionDelay,
//                miscellaneousParameterOptions.CurrentValue, timeParameterOptions.CurrentValue, stateListLengthOptions.CurrentValue, maxOperationsPerBlockOptions.CurrentValue,
//                beaconChainUtility, beaconStateAccessor, beaconStateTransition);

//            state.CurrentEpochAttestations.Count.ShouldBe(1);
//            var shard = attestation.Data.Crosslink.Shard;
//            var preCrosslink = Crosslink.Clone(state.CurrentCrosslinks[(int)(ulong)shard]);

//            // Act
//            RunProcessCrosslinks(beaconStateTransition, timeParameterOptions.CurrentValue, state);

//            // Assert
//            var crosslinkIndex = (int)(ulong)shard;
//            state.PreviousCrosslinks[crosslinkIndex].ShouldNotBe(state.CurrentCrosslinks[crosslinkIndex]);
//            preCrosslink.ShouldNotBe(state.CurrentCrosslinks[crosslinkIndex]);
//        }

//        private void RunProcessCrosslinks(BeaconStateTransition beaconStateTransition, TimeParameters timeParameters, BeaconState state)
//        {
//            TestProcessUtility.RunEpochProcessingWith(beaconStateTransition, timeParameters, state, "process_crosslinks");
//        }
//    }
//}
