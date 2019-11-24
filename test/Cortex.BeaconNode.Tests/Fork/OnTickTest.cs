using System;
using System.Linq;
using Cortex.BeaconNode.Configuration;
using Cortex.BeaconNode.Data;
using Cortex.BeaconNode.Tests.Helpers;
using Cortex.Containers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Shouldly;

namespace Cortex.BeaconNode.Tests.Fork
{
    [TestClass]
    public class OnTickTest
    {
        [TestMethod]
        public void BasicOnTick()
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
            (var beaconChainUtility, _, _, _, var state) = TestState.PrepareTestState(chainConstants, miscellaneousParameterOptions, gweiValueOptions, initialValueOptions, timeParameterOptions, stateListLengthOptions, rewardsAndPenaltiesOptions, maxOperationsPerBlockOptions);

            var loggerFactory = new LoggerFactory(new[] {
                new ConsoleLoggerProvider(TestOptionsMonitor.Create(new ConsoleLoggerOptions()))
            });
            var storeProvider = new StoreProvider(loggerFactory, timeParameterOptions, beaconChainUtility);
            var forkChoice = new ForkChoice(loggerFactory.CreateLogger<ForkChoice>(), miscellaneousParameterOptions, initialValueOptions, timeParameterOptions, stateListLengthOptions, maxOperationsPerBlockOptions,
                beaconChainUtility, storeProvider);
            var store = forkChoice.GetGenesisStore(state);

            // Act
            RunOnTick(store, store.Time + 1, expectNewJustifiedCheckpoint:false, forkChoice);

            // Assert
        }

        [TestMethod]
        public void UpdateJustifiedSingle()
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
            (var beaconChainUtility, _, _, _, var state) = TestState.PrepareTestState(chainConstants, miscellaneousParameterOptions, gweiValueOptions, initialValueOptions, timeParameterOptions, stateListLengthOptions, rewardsAndPenaltiesOptions, maxOperationsPerBlockOptions);

            var loggerFactory = new LoggerFactory(new[] {
                new ConsoleLoggerProvider(TestOptionsMonitor.Create(new ConsoleLoggerOptions()))
            });
            var storeProvider = new StoreProvider(loggerFactory, timeParameterOptions, beaconChainUtility);
            var forkChoice = new ForkChoice(loggerFactory.CreateLogger<ForkChoice>(), miscellaneousParameterOptions, initialValueOptions, timeParameterOptions, stateListLengthOptions, maxOperationsPerBlockOptions,
                beaconChainUtility, storeProvider);
            var store = forkChoice.GetGenesisStore(state);

            var timeParameters = timeParameterOptions.CurrentValue;
            var secondsPerEpoch = timeParameters.SecondsPerSlot * (ulong)timeParameters.SlotsPerEpoch;
            var checkpoint = new Checkpoint(
                store.JustifiedCheckpoint.Epoch + new Epoch(1),
                new Hash32(Enumerable.Repeat((byte)0x55, 32).ToArray()));
            store.SetBestJustifiedCheckpoint(checkpoint);

            // Act
            RunOnTick(store, store.Time + 1, expectNewJustifiedCheckpoint: false, forkChoice);

            // Assert
        }

        private void RunOnTick(IStore store, ulong time, bool expectNewJustifiedCheckpoint, ForkChoice forkChoice)
        {
            var previousJustifiedCheckpoint = store.JustifiedCheckpoint;

            forkChoice.OnTick(store, time);

            store.Time.ShouldBe(time);

            if (expectNewJustifiedCheckpoint)
            {
                store.JustifiedCheckpoint.ShouldBe(store.BestJustifiedCheckpoint);
                store.JustifiedCheckpoint.Epoch.ShouldBeGreaterThan(previousJustifiedCheckpoint.Epoch);
                store.JustifiedCheckpoint.Root.ShouldNotBe(previousJustifiedCheckpoint.Root);
            }
            else
            {
                store.JustifiedCheckpoint.ShouldBe(previousJustifiedCheckpoint);
            }
        }
    }
}
