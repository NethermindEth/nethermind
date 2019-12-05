using System;
using System.Linq;
using Cortex.BeaconNode.Configuration;
using Cortex.BeaconNode.Storage;
using Cortex.BeaconNode.Tests.Helpers;
using Cortex.Containers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
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
            var testServiceProvider = TestSystem.BuildTestServiceProvider(useStore: true);
            var state = TestState.PrepareTestState(testServiceProvider);

            var forkChoice = testServiceProvider.GetService<ForkChoice>();
            var store = forkChoice.GetGenesisStore(state);

            // Act
            RunOnTick(testServiceProvider, store, store.Time + 1, expectNewJustifiedCheckpoint: false);

            // Assert
        }

        [TestMethod]
        public void UpdateJustifiedSingle()
        {
            // Arrange
            var testServiceProvider = TestSystem.BuildTestServiceProvider(useStore: true);
            var state = TestState.PrepareTestState(testServiceProvider);

            var forkChoice = testServiceProvider.GetService<ForkChoice>();
            var store = forkChoice.GetGenesisStore(state);

            var timeParameters = testServiceProvider.GetService<IOptions<TimeParameters>>().Value;

            var secondsPerEpoch = timeParameters.SecondsPerSlot * (ulong)timeParameters.SlotsPerEpoch;
            var checkpoint = new Checkpoint(
                store.JustifiedCheckpoint.Epoch + new Epoch(1),
                new Hash32(Enumerable.Repeat((byte)0x55, 32).ToArray()));
            store.SetBestJustifiedCheckpoint(checkpoint);

            // Act
            RunOnTick(testServiceProvider, store, store.Time + secondsPerEpoch, expectNewJustifiedCheckpoint: true);

            // Assert
        }

        private void RunOnTick(IServiceProvider testServiceProvider, IStore store, ulong time, bool expectNewJustifiedCheckpoint)
        {
            var forkChoice = testServiceProvider.GetService<ForkChoice>();

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
