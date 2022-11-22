// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nethermind.BeaconNode.Test.Helpers;
using Nethermind.Core2;
using Nethermind.Core2.Containers;
using Nethermind.Core2.Crypto;
using Nethermind.Core2.Types;
using Shouldly;

namespace Nethermind.BeaconNode.Test.BlockProcessing
{
    [TestClass]
    public class ProcessBlockHeaderTest
    {
        [TestMethod]
        public void SuccessBlockHeader()
        {
            // Arrange
            IServiceProvider testServiceProvider = TestSystem.BuildTestServiceProvider();
            BeaconState state = TestState.PrepareTestState(testServiceProvider);

            BeaconBlock block = TestBlock.BuildEmptyBlockForNextSlot(testServiceProvider, state, BlsSignature.Zero);

            RunBlockHeaderProcessing(testServiceProvider, state, block, expectValid: true);
        }

        [TestMethod]
        public void InvalidSlotBlockHeader()
        {
            // Arrange
            IServiceProvider testServiceProvider = TestSystem.BuildTestServiceProvider();
            BeaconState state = TestState.PrepareTestState(testServiceProvider);

            BeaconBlock block = TestBlock.BuildEmptyBlock(testServiceProvider, state, state.Slot + new Slot(2), BlsSignature.Zero);

            RunBlockHeaderProcessing(testServiceProvider, state, block, expectValid: false);
        }

        // Run ``process_block_header``, yielding:
        //  - pre-state('pre')
        //  - block('block')
        //  - post-state('post').
        // If ``valid == False``, run expecting ``AssertionError``
        private void RunBlockHeaderProcessing(IServiceProvider testServiceProvider, BeaconState state, BeaconBlock block, bool expectValid)
        {
            ICryptographyService cryptographyService = testServiceProvider.GetService<ICryptographyService>();
            BeaconStateTransition beaconStateTransition = testServiceProvider.GetService<BeaconStateTransition>();

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
