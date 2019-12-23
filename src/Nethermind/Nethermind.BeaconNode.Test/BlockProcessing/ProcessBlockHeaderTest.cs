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
                beaconStateTransition.ProcessBlockHeader(state, block, validateStateRoot: true);
            }
            else
            {
                Should.Throw<Exception>(() =>
                {
                    beaconStateTransition.ProcessBlockHeader(state, block, validateStateRoot: true);
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
