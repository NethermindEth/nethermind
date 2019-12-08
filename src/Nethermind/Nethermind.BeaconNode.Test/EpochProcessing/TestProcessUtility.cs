using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Nethermind.BeaconNode.Configuration;
using Nethermind.BeaconNode.Containers;
using Nethermind.Core2.Types;

namespace Nethermind.BeaconNode.Tests.EpochProcessing
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
