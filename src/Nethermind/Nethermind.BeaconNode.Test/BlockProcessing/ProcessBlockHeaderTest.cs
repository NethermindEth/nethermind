using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nethermind.BeaconNode.Containers;
using Nethermind.BeaconNode.Tests.Helpers;
using Nethermind.Core2.Types;
using Shouldly;

namespace Nethermind.BeaconNode.Tests.BlockProcessing
{
    [TestClass]
    public class ProcessBlockHeaderTest
    {
        [TestMethod]
        public void SuccessBlockHeader()
        {
            // Arrange
            var testServiceProvider = TestSystem.BuildTestServiceProvider();
            var state = TestState.PrepareTestState(testServiceProvider);

            var block = TestBlock.BuildEmptyBlockForNextSlot(testServiceProvider, state, signed: true);

            RunBlockHeaderProcessing(testServiceProvider, state, block, expectValid: true);
        }

        [TestMethod]
        public void InvalidSignatureBlockHeader()
        {
            // Arrange
            var testServiceProvider = TestSystem.BuildTestServiceProvider();
            var state = TestState.PrepareTestState(testServiceProvider);

            var block = TestBlock.BuildEmptyBlockForNextSlot(testServiceProvider, state, signed: false);

            RunBlockHeaderProcessing(testServiceProvider, state, block, expectValid: false);
        }

        // Run ``process_block_header``, yielding:
        //  - pre-state('pre')
        //  - block('block')
        //  - post-state('post').
        // If ``valid == False``, run expecting ``AssertionError``
        private void RunBlockHeaderProcessing(IServiceProvider testServiceProvider, BeaconState state, BeaconBlock block, bool expectValid)
        {
            var beaconStateTransition = testServiceProvider.GetService<BeaconStateTransition>();

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
            beaconStateTransition.ProcessSlots(state, state.Slot + Slot.One);
        }
    }
}
