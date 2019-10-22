using System;
using System.Collections;
using System.Linq;
using Cortex.BeaconNode.Configuration;
using Cortex.Containers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Shouldly;

namespace Cortex.BeaconNode.Tests.EpochProcessing
{
    [TestClass]
    public class ProcessCrosslinksTest
    {
        [TestMethod]
        public void NoAttestations()
        {
            // Arrange
            var loggerFactory = new LoggerFactory(new[] {
                new ConsoleLoggerProvider(TestOptionsMonitor.Create(new ConsoleLoggerOptions()))
            });
            TestConfiguration.GetMinimalConfiguration(
                out var chainConstants,
                out var miscellaneousParameterOptions,
                out var gweiValueOptions,
                out var initialValueOptions,
                out var timeParameterOptions,
                out var stateListLengthOptions,
                out var maxOperationsPerBlockOptions);
            var cryptographyService = new CryptographyService();
            var beaconChainUtility = new BeaconChainUtility(miscellaneousParameterOptions, timeParameterOptions, cryptographyService);
            var beaconStateAccessor = new BeaconStateAccessor(miscellaneousParameterOptions, initialValueOptions, timeParameterOptions, stateListLengthOptions, cryptographyService, beaconChainUtility);
            var beaconStateTransition = new BeaconStateTransition(loggerFactory.CreateLogger<BeaconStateTransition>(), miscellaneousParameterOptions, initialValueOptions, timeParameterOptions, stateListLengthOptions, maxOperationsPerBlockOptions, beaconChainUtility, beaconStateAccessor);

            var numberOfValidators = (ulong)timeParameterOptions.CurrentValue.SlotsPerEpoch * 10;
            var state = TestData.CreateGenesisState(chainConstants, miscellaneousParameterOptions.CurrentValue, initialValueOptions.CurrentValue, gweiValueOptions.CurrentValue, timeParameterOptions.CurrentValue, stateListLengthOptions.CurrentValue, maxOperationsPerBlockOptions.CurrentValue, numberOfValidators);

            // Skip ahead to just before epoch
            var nextEpoch = new Epoch(1);
            var slot = new Slot((ulong)timeParameterOptions.CurrentValue.SlotsPerEpoch * (ulong)nextEpoch - 1);
            state.SetSlot(slot);

            // Act
            RunProcessCrosslinks(beaconStateTransition, timeParameterOptions.CurrentValue, state);

            // Assert
            for (var shard = 0; shard < (int)(ulong)miscellaneousParameterOptions.CurrentValue.ShardCount; shard++)
            {
                state.PreviousCrosslinks[shard].ShouldBe(state.CurrentCrosslinks[shard], $"Shard {shard}");
            }
        }

        private void RunProcessCrosslinks(BeaconStateTransition beaconStateTransition, TimeParameters timeParameters, BeaconState state)
        {
            TestProcessUtility.RunEpochProcessingWith(beaconStateTransition, timeParameters, state, "process_crosslinks");
        }
    }
}
