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
using Nethermind.Core2.Containers;
using Nethermind.Core2.Types;

namespace Nethermind.BeaconNode.Test.EpochProcessing
{
    public static class TestProcessUtility
    {
        /// <summary>
        /// Processes to the next epoch transition, up to, but not including, the sub-transition named ``process_name``
        /// </summary>
        public static void RunEpochProcessingTo(IServiceProvider testServiceProvider, BeaconState state, TestProcessStep step)
        {
            TimeParameters timeParameters = testServiceProvider.GetService<IOptions<TimeParameters>>().Value;
            BeaconStateTransition beaconStateTransition = testServiceProvider.GetService<BeaconStateTransition>();

            Slot slot = (Slot)(state.Slot + (timeParameters.SlotsPerEpoch - state.Slot % timeParameters.SlotsPerEpoch) - 1UL);

            // transition state to slot before epoch state transition
            beaconStateTransition.ProcessSlots(state, slot);

            // start transitioning, do one slot update before the epoch itself.
            beaconStateTransition.ProcessSlot(state);

            // process components of epoch transition before final-updates
            if (step == TestProcessStep.ProcessJustificationAndFinalization)
            {
                return;
            }
            // Note: only run when present. Later phases introduce more to the epoch-processing.
            beaconStateTransition.ProcessJustificationAndFinalization(state);

            if (step == TestProcessStep.ProcessRewardsAndPenalties)
            {
                return;
            }

            beaconStateTransition.ProcessRewardsAndPenalties(state);

            if (step == TestProcessStep.ProcessRegistryUpdates)
            {
                return;
            }

            beaconStateTransition.ProcessRegistryUpdates(state);

            if (step == TestProcessStep.ProcessSlashings)
            {
                return;
            }

            beaconStateTransition.ProcessSlashings(state);

            if (step == TestProcessStep.ProcessFinalUpdates)
            {
                return;
            }

            beaconStateTransition.ProcessFinalUpdates(state);
        }

        /// <summary>
        /// Processes to the next epoch transition, up to and including the sub-transition named ``process_name``
        /// - pre-state('pre'), state before calling ``process_name``
        /// - post-state('post'), state after calling ``process_name``
        /// </summary>
        public static void RunEpochProcessingWith(IServiceProvider testServiceProvider, BeaconState state, TestProcessStep step)
        {
            RunEpochProcessingTo(testServiceProvider, state, step);

            BeaconStateTransition beaconStateTransition = testServiceProvider.GetService<BeaconStateTransition>();

            if (step == TestProcessStep.ProcessJustificationAndFinalization)
            {
                beaconStateTransition.ProcessJustificationAndFinalization(state);
                return;
            }
            if (step == TestProcessStep.ProcessRewardsAndPenalties)
            {
                beaconStateTransition.ProcessRewardsAndPenalties(state);
                return;
            }
            if (step == TestProcessStep.ProcessRegistryUpdates)
            {
                beaconStateTransition.ProcessRegistryUpdates(state);
                return;
            }
            if (step == TestProcessStep.ProcessSlashings)
            {
                beaconStateTransition.ProcessSlashings(state);
                return;
            }
            if (step == TestProcessStep.ProcessFinalUpdates)
            {
                beaconStateTransition.ProcessFinalUpdates(state);
                return;
            }

            throw new NotImplementedException();
        }
    }
}
