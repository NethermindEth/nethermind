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
