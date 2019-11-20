using System;
using Cortex.BeaconNode.Tests.Helpers;
using Cortex.Containers;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Shouldly;

namespace Cortex.BeaconNode.Tests.BlockProcessing
{
    [TestClass]
    public class ProcessBlockHeaderTest
    {
        [TestMethod]
        public void SuccessBlockHeader()
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
            (var beaconChainUtility, var beaconStateAccessor, var _, var beaconStateTransition, var state) = TestState.PrepareTestState(chainConstants, miscellaneousParameterOptions, gweiValueOptions, initialValueOptions, timeParameterOptions, stateListLengthOptions, rewardsAndPenaltiesOptions, maxOperationsPerBlockOptions);

            var block = TestBlock.BuildEmptyBlockForNextSlot(state, signed: true,
                miscellaneousParameterOptions.CurrentValue, timeParameterOptions.CurrentValue, stateListLengthOptions.CurrentValue, maxOperationsPerBlockOptions.CurrentValue,
                beaconChainUtility, beaconStateAccessor, beaconStateTransition);

            RunBlockHeaderProcessing(state, block, expectValid: true,
                beaconStateTransition);
        }

        [TestMethod]
        public void InvalidSignatureBlockHeader()
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
            (var beaconChainUtility, var beaconStateAccessor, var _, var beaconStateTransition, var state) = TestState.PrepareTestState(chainConstants, miscellaneousParameterOptions, gweiValueOptions, initialValueOptions, timeParameterOptions, stateListLengthOptions, rewardsAndPenaltiesOptions, maxOperationsPerBlockOptions);

            var block = TestBlock.BuildEmptyBlockForNextSlot(state, signed: false,
                miscellaneousParameterOptions.CurrentValue, timeParameterOptions.CurrentValue, stateListLengthOptions.CurrentValue, maxOperationsPerBlockOptions.CurrentValue,
                beaconChainUtility, beaconStateAccessor, beaconStateTransition);

            RunBlockHeaderProcessing(state, block, expectValid: false,
                beaconStateTransition);
        }

        // Run ``process_block_header``, yielding:
        //  - pre-state('pre')
        //  - block('block')
        //  - post-state('post').
        // If ``valid == False``, run expecting ``AssertionError``
        private void RunBlockHeaderProcessing(BeaconState state, BeaconBlock block, bool expectValid,
            BeaconStateTransition beaconStateTransition)
        {
            PrepareStateForHeaderProcessing(state,
                beaconStateTransition);

            if (expectValid)
            {
                beaconStateTransition.ProcessBlockHeader(state, block);
            }
            else
            {
                Should.Throw<Exception>(() =>
                {
                    beaconStateTransition.ProcessBlockHeader(state, block);
                });
            }
        }

        private void PrepareStateForHeaderProcessing(BeaconState state,
            BeaconStateTransition beaconStateTransition)
        {
            beaconStateTransition.ProcessSlots(state, state.Slot + new Slot(1));
        }
    }
}
