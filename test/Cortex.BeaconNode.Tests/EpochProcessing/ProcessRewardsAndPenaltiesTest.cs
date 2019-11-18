using System;
using System.Collections;
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

        private void RunProcessRewardsAndPenalties(BeaconStateTransition beaconStateTransition, TimeParameters timeParameters, BeaconState state)
        {
            TestProcessUtility.RunEpochProcessingWith(beaconStateTransition, timeParameters, state, TestProcessStep.ProcessRewardsAndPenalties);
        }
    }
}
