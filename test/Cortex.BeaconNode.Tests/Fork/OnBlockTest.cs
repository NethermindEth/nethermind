using System;
using System.Linq;
using Cortex.BeaconNode.Configuration;
using Cortex.BeaconNode.Data;
using Cortex.BeaconNode.Ssz;
using Cortex.BeaconNode.Tests.Helpers;
using Cortex.Containers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Shouldly;

namespace Cortex.BeaconNode.Tests.Fork
{
    [TestClass]
    public class OnBlockTest
    {
        [TestMethod]
        public void BasicOnBlock()
        {
            // Arrange
            TestConfiguration.GetMinimalConfiguration(
                out var chainConstants,
                out var miscellaneousParameterOptions,
                out var gweiValueOptions,
                out var initialValueOptions,
                out var timeParameterOptions,
                out var stateListLengthOptions,
                out var rewardsAndPenaltiesOptions,
                out var maxOperationsPerBlockOptions);
            (var beaconChainUtility, var beaconStateAccessor, _, var beaconStateTransition, var state) = TestState.PrepareTestState(chainConstants, miscellaneousParameterOptions, gweiValueOptions, initialValueOptions, timeParameterOptions, stateListLengthOptions, rewardsAndPenaltiesOptions, maxOperationsPerBlockOptions);

            var loggerFactory = new LoggerFactory(new[] {
                new ConsoleLoggerProvider(TestOptionsMonitor.Create(new ConsoleLoggerOptions()))
            });
            var storeProvider = new StoreProvider(loggerFactory, timeParameterOptions, beaconChainUtility);
            var forkChoice = new ForkChoice(loggerFactory.CreateLogger<ForkChoice>(), miscellaneousParameterOptions, initialValueOptions, timeParameterOptions, stateListLengthOptions, maxOperationsPerBlockOptions,
                beaconChainUtility, storeProvider);

            // Initialization
            var timeParameters = timeParameterOptions.CurrentValue;
            var store = forkChoice.GetGenesisStore(state);
            var time = 100uL;
            forkChoice.OnTick(store, time);
            store.Time.ShouldBe(time);

            // On receiving a block of `GENESIS_SLOT + 1` slot
            var block = TestBlock.BuildEmptyBlockForNextSlot(state, signed:false,
                miscellaneousParameterOptions.CurrentValue, timeParameters, stateListLengthOptions.CurrentValue, maxOperationsPerBlockOptions.CurrentValue,
                beaconChainUtility, beaconStateAccessor, beaconStateTransition);
            TestState.StateTransitionAndSignBlock(state, block,
                miscellaneousParameterOptions.CurrentValue, timeParameters, stateListLengthOptions.CurrentValue, maxOperationsPerBlockOptions.CurrentValue,
                beaconChainUtility, beaconStateAccessor, beaconStateTransition);
            RunOnBlock(store, block, expectValid: true, 
                miscellaneousParameterOptions.CurrentValue, maxOperationsPerBlockOptions.CurrentValue,
                forkChoice);

            //  On receiving a block of next epoch
            var time2 = time + timeParameters.SecondsPerSlot * (ulong)timeParameters.SlotsPerEpoch;
            store.SetTime(time2);
            var block2 = TestBlock.BuildEmptyBlockForNextSlot(state, signed: false,
                miscellaneousParameterOptions.CurrentValue, timeParameterOptions.CurrentValue, stateListLengthOptions.CurrentValue, maxOperationsPerBlockOptions.CurrentValue,
                beaconChainUtility, beaconStateAccessor, beaconStateTransition);
            var slot2 = block.Slot + timeParameters.SlotsPerEpoch;
            block2.SetSlot(slot2);
            TestState.StateTransitionAndSignBlock(state, block2,
                miscellaneousParameterOptions.CurrentValue, timeParameterOptions.CurrentValue, stateListLengthOptions.CurrentValue, maxOperationsPerBlockOptions.CurrentValue,
                beaconChainUtility, beaconStateAccessor, beaconStateTransition);

            RunOnBlock(store, block2, expectValid: true,
                miscellaneousParameterOptions.CurrentValue, maxOperationsPerBlockOptions.CurrentValue,
                forkChoice);

            // Assert
            // TODO: add tests for justified_root and finalized_root
        }

        private void RunOnBlock(IStore store, BeaconBlock block, bool expectValid,
            MiscellaneousParameters miscellaneousParameters, MaxOperationsPerBlock maxOperationsPerBlock,
            ForkChoice forkChoice)
        {
            if (!expectValid)
            {
                Should.Throw<Exception>(() =>
                {
                    forkChoice.OnBlock(store, block);
                });
                return;
            }

            forkChoice.OnBlock(store, block);
            var signingRoot = block.SigningRoot(miscellaneousParameters, maxOperationsPerBlock);
            var storeBlock = store.Blocks[signingRoot];
            storeBlock.ShouldBe(block);
        }
    }
}
