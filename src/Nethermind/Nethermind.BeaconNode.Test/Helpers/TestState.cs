using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Nethermind.BeaconNode.Configuration;
using Nethermind.BeaconNode.Containers;
using Nethermind.BeaconNode.Ssz;
using Nethermind.Core2.Types;

namespace Nethermind.BeaconNode.Tests.Helpers
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
            var timeParameters = testServiceProvider.GetService<IOptions<TimeParameters>>().Value;
            var beaconStateTransition = testServiceProvider.GetService<BeaconStateTransition>();

            Slot slot = (Slot)(state.Slot + timeParameters.SlotsPerEpoch - state.Slot % timeParameters.SlotsPerEpoch);
            beaconStateTransition.ProcessSlots(state, slot);
        }

        /// <summary>
        /// Transition to the next slot.
        /// </summary>
        public static void NextSlot(IServiceProvider testServiceProvider, BeaconState state)
        {
            var beaconStateTransition = testServiceProvider.GetService<BeaconStateTransition>();

            var slot = state.Slot + new Slot(1);
            beaconStateTransition.ProcessSlots(state, slot);
        }

        public static BeaconState PrepareTestState(IServiceProvider testServiceProvider)
        {
            var timeParameters = testServiceProvider.GetService<IOptions<TimeParameters>>().Value;
            var numberOfValidators = (ulong)timeParameters.SlotsPerEpoch * 10;
            var state = TestGenesis.CreateGenesisState(testServiceProvider, numberOfValidators);

            return state;
        }

        /// <summary>
        /// State transition via the provided ``block``
        /// then package the block with the state root and signature.
        /// </summary>
        public static void StateTransitionAndSignBlock(IServiceProvider testServiceProvider, BeaconState state, BeaconBlock block)
        {
            var miscellaneousParameters = testServiceProvider.GetService<IOptions<MiscellaneousParameters>>().Value;
            var timeParameters = testServiceProvider.GetService<IOptions<TimeParameters>>().Value;
            var stateListLengths = testServiceProvider.GetService<IOptions<StateListLengths>>().Value;
            var maxOperationsPerBlock = testServiceProvider.GetService<IOptions<MaxOperationsPerBlock>>().Value;

            var beaconStateTransition = testServiceProvider.GetService<BeaconStateTransition>();

            beaconStateTransition.StateTransition(state, block, validateStateRoot: false);
            var stateRoot = state.HashTreeRoot(miscellaneousParameters, timeParameters, stateListLengths, maxOperationsPerBlock);
            block.SetStateRoot(stateRoot);
            TestBlock.SignBlock(testServiceProvider, state, block, ValidatorIndex.None);
        }
    }
}
