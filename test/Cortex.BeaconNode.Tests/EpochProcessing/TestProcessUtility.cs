using System;
using Cortex.BeaconNode.Configuration;
using Cortex.Containers;

namespace Cortex.BeaconNode.Tests.EpochProcessing
{
    public static class TestProcessUtility
    {
        /// <summary>
        /// Processes to the next epoch transition, up to and including the sub-transition named ``process_name``
        /// - pre-state('pre'), state before calling ``process_name``
        /// - post-state('post'), state after calling ``process_name``
        /// </summary>
        public static void RunEpochProcessingWith(BeaconStateTransition beaconStateTransition, TimeParameters timeParameters, BeaconState state, TestProcessStep step)
        {
            RunEpochProcessingTo(beaconStateTransition, timeParameters, state, step);

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

            throw new NotImplementedException();
        }

        /// <summary>
        /// Processes to the next epoch transition, up to, but not including, the sub-transition named ``process_name``
        /// </summary>
        private static void RunEpochProcessingTo(BeaconStateTransition beaconStateTransition, TimeParameters timeParameters, BeaconState state, TestProcessStep step)
        {
            var slot = state.Slot + (timeParameters.SlotsPerEpoch - state.Slot % timeParameters.SlotsPerEpoch);

            // transition state to slot before epoch state transition
            beaconStateTransition.ProcessSlots(state, slot - new Slot(1));

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

            throw new NotImplementedException();
        }
    }
}
