using System;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nethermind.BeaconNode.Configuration;
using Nethermind.BeaconNode.Containers;
using Nethermind.BeaconNode.Storage;
using Nethermind.BeaconNode.Tests.Helpers;
using Nethermind.Core2.Types;
using Shouldly;

namespace Nethermind.BeaconNode.Tests.Fork
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
                store.JustifiedCheckpoint.Epoch + Epoch.One,
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
