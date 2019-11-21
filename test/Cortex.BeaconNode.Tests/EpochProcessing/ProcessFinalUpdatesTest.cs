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
    public class ProcessFinalUpdatesTest
    {
        [TestMethod]
        public void Eth1VoteNoReset()
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
            (var beaconChainUtility, var beaconStateAccessor, var beaconStateMutator, var beaconStateTransition, var state) = TestState.PrepareTestState(chainConstants, miscellaneousParameterOptions, gweiValueOptions, initialValueOptions, timeParameterOptions, stateListLengthOptions, rewardsAndPenaltiesOptions, maxOperationsPerBlockOptions);

            var timeParameters = timeParameterOptions.CurrentValue;
            timeParameters.SlotsPerEth1VotingPeriod.ShouldBeGreaterThan(timeParameters.SlotsPerEpoch);

            // skip ahead to the end of the epoch
            state.SetSlot(timeParameters.SlotsPerEpoch - new Slot(1));

            // add a vote for each skipped slot.
            for (var index = Slot.Zero; index < state.Slot + new Slot(1); index += new Slot(1))
            {
                var eth1DepositIndex = state.Eth1DepositIndex;
                var depositRoot = new Hash32(Enumerable.Repeat((byte)0xaa, 32).ToArray());
                var blockHash = new Hash32(Enumerable.Repeat((byte)0xbb, 32).ToArray());
                var eth1Data = new Eth1Data(depositRoot, eth1DepositIndex, blockHash);
                state.AppendEth1DataVotes(eth1Data);
            }

            // Act
            RunProcessFinalUpdates(beaconStateTransition, timeParameterOptions.CurrentValue, state);

            // Assert
            state.Eth1DataVotes.Count.ShouldBe((int)(ulong)timeParameters.SlotsPerEpoch);
        }

        [TestMethod]
        public void Eth1VoteReset()
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
            (var beaconChainUtility, var beaconStateAccessor, var beaconStateMutator, var beaconStateTransition, var state) = TestState.PrepareTestState(chainConstants, miscellaneousParameterOptions, gweiValueOptions, initialValueOptions, timeParameterOptions, stateListLengthOptions, rewardsAndPenaltiesOptions, maxOperationsPerBlockOptions);

            var timeParameters = timeParameterOptions.CurrentValue;

            //  skip ahead to the end of the voting period
            state.SetSlot(timeParameters.SlotsPerEth1VotingPeriod - new Slot(1));

            // add a vote for each skipped slot.
            for (var index = Slot.Zero; index < state.Slot + new Slot(1); index += new Slot(1))
            {
                var eth1DepositIndex = state.Eth1DepositIndex;
                var depositRoot = new Hash32(Enumerable.Repeat((byte)0xaa, 32).ToArray());
                var blockHash = new Hash32(Enumerable.Repeat((byte)0xbb, 32).ToArray());
                var eth1Data = new Eth1Data(depositRoot, eth1DepositIndex, blockHash);
                state.AppendEth1DataVotes(eth1Data);
            }

            // Act
            RunProcessFinalUpdates(beaconStateTransition, timeParameterOptions.CurrentValue, state);

            // Assert
            state.Eth1DataVotes.Count.ShouldBe(0);
        }

        private void RunProcessFinalUpdates(BeaconStateTransition beaconStateTransition, TimeParameters timeParameters, BeaconState state)
        {
            TestProcessUtility.RunEpochProcessingWith(beaconStateTransition, timeParameters, state, TestProcessStep.ProcessFinalUpdates);
        }
    }
}
