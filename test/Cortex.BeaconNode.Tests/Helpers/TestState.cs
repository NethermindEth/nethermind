using Cortex.BeaconNode.Configuration;
using Cortex.Containers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;

namespace Cortex.BeaconNode.Tests.Helpers
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
        public static void NextEpoch(BeaconState state, TimeParameters timeParameters, BeaconStateTransition beaconStateTransition)
        {
            var slot = state.Slot + timeParameters.SlotsPerEpoch - (state.Slot % timeParameters.SlotsPerEpoch);
            beaconStateTransition.ProcessSlots(state, slot);
        }

        /// <summary>
        /// Transition to the next slot.
        /// </summary>
        public static void NextSlot(BeaconState state, BeaconStateTransition beaconStateTransition)
        {
            var slot = state.Slot + new Slot(1);
            beaconStateTransition.ProcessSlots(state, slot);
        }

        public static (BeaconChainUtility, BeaconStateAccessor, BeaconStateTransition, BeaconState) PrepareTestState(ChainConstants chainConstants,
            IOptionsMonitor<MiscellaneousParameters> miscellaneousParameterOptions,
            IOptionsMonitor<GweiValues> gweiValueOptions,
            IOptionsMonitor<InitialValues> initialValueOptions,
            IOptionsMonitor<TimeParameters> timeParameterOptions,
            IOptionsMonitor<StateListLengths> stateListLengthOptions,
            IOptionsMonitor<RewardsAndPenalties> rewardsAndPenaltiesOptions,
            IOptionsMonitor<MaxOperationsPerBlock> maxOperationsPerBlockOptions)
        {
            var loggerFactory = new LoggerFactory(new[] {
                new ConsoleLoggerProvider(TestOptionsMonitor.Create(new ConsoleLoggerOptions()))
            });
            var cryptographyService = new CryptographyService();
            var beaconChainUtility = new BeaconChainUtility(loggerFactory.CreateLogger<BeaconChainUtility>(),
                miscellaneousParameterOptions, gweiValueOptions, timeParameterOptions,
                cryptographyService);
            var beaconStateAccessor = new BeaconStateAccessor(miscellaneousParameterOptions, initialValueOptions, timeParameterOptions, stateListLengthOptions,
                cryptographyService, beaconChainUtility);
            var beaconStateMutator = new BeaconStateMutator(chainConstants, timeParameterOptions, stateListLengthOptions, rewardsAndPenaltiesOptions,
                beaconChainUtility, beaconStateAccessor);
            var beaconStateTransition = new BeaconStateTransition(loggerFactory.CreateLogger<BeaconStateTransition>(),
                chainConstants, miscellaneousParameterOptions, gweiValueOptions, initialValueOptions, timeParameterOptions, stateListLengthOptions, rewardsAndPenaltiesOptions, maxOperationsPerBlockOptions,
                cryptographyService, beaconChainUtility, beaconStateAccessor, beaconStateMutator);
            var numberOfValidators = (ulong)timeParameterOptions.CurrentValue.SlotsPerEpoch * 10;
            var state = TestGenesis.CreateGenesisState(chainConstants, miscellaneousParameterOptions.CurrentValue, initialValueOptions.CurrentValue, gweiValueOptions.CurrentValue, timeParameterOptions.CurrentValue, stateListLengthOptions.CurrentValue, maxOperationsPerBlockOptions.CurrentValue, numberOfValidators);

            return (beaconChainUtility, beaconStateAccessor, beaconStateTransition, state);
        }
    }
}
