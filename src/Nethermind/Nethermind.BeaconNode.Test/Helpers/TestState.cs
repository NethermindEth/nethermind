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
using Nethermind.Core2.Configuration;
using Nethermind.Core2;
using Nethermind.Core2.Containers;
using Nethermind.Core2.Crypto;
using Nethermind.Core2.Types;

namespace Nethermind.BeaconNode.Test.Helpers
{
    public static class TestState
    {
        public static Gwei GetBalance(BeaconState state, ValidatorIndex proposerIndex)
        {
            return state.Balances[(int)(ulong)proposerIndex];
        }

        /// <summary>
        /// Transition to the start slot of the next epoch
        /// </summary>
        public static void NextEpoch(IServiceProvider testServiceProvider, BeaconState state)
        {
            TimeParameters timeParameters = testServiceProvider.GetService<IOptions<TimeParameters>>().Value;
            BeaconStateTransition beaconStateTransition = testServiceProvider.GetService<BeaconStateTransition>();

            Slot slot = (Slot)(state.Slot + timeParameters.SlotsPerEpoch - state.Slot % timeParameters.SlotsPerEpoch);
            beaconStateTransition.ProcessSlots(state, slot);
        }

        /// <summary>
        /// Transition to the next slot.
        /// </summary>
        public static void NextSlot(IServiceProvider testServiceProvider, BeaconState state)
        {
            BeaconStateTransition beaconStateTransition = testServiceProvider.GetService<BeaconStateTransition>();

            Slot slot = state.Slot + new Slot(1);
            beaconStateTransition.ProcessSlots(state, slot);
        }

        public static BeaconState PrepareTestState(IServiceProvider testServiceProvider)
        {
            TimeParameters timeParameters = testServiceProvider.GetService<IOptions<TimeParameters>>().Value;
            ulong numberOfValidators = (ulong)timeParameters.SlotsPerEpoch * 10;
            BeaconState state = TestGenesis.CreateGenesisState(testServiceProvider, numberOfValidators);

            return state;
        }

        /// <summary>
        /// State transition via the provided ``block``
        /// then package the block with the state root and signature.
        /// </summary>
        public static void StateTransitionAndSignBlock(IServiceProvider testServiceProvider, BeaconState state, BeaconBlock block)
        {
            MiscellaneousParameters miscellaneousParameters = testServiceProvider.GetService<IOptions<MiscellaneousParameters>>().Value;
            TimeParameters timeParameters = testServiceProvider.GetService<IOptions<TimeParameters>>().Value;
            StateListLengths stateListLengths = testServiceProvider.GetService<IOptions<StateListLengths>>().Value;
            MaxOperationsPerBlock maxOperationsPerBlock = testServiceProvider.GetService<IOptions<MaxOperationsPerBlock>>().Value;

            ICryptographyService cryptographyService = testServiceProvider.GetService<ICryptographyService>();
            BeaconStateTransition beaconStateTransition = testServiceProvider.GetService<BeaconStateTransition>();

            beaconStateTransition.StateTransition(state, block, validateStateRoot: false);
            Hash32 stateRoot = cryptographyService.HashTreeRoot(state);
            block.SetStateRoot(stateRoot);
            TestBlock.SignBlock(testServiceProvider, state, block, ValidatorIndex.None);
        }
    }
}
