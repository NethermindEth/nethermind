// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nethermind.Core2.Configuration;
using Nethermind.BeaconNode.Storage;
using Nethermind.BeaconNode.Test.Helpers;
using Nethermind.Core2;
using Nethermind.Core2.Containers;
using Nethermind.Core2.Crypto;
using Nethermind.Core2.Types;
using Shouldly;

namespace Nethermind.BeaconNode.Test.ForkTests
{
    [TestClass]
    public class OnTickTest
    {
        [TestMethod]
        public async Task BasicOnTick()
        {
            // Arrange
            IServiceProvider testServiceProvider = TestSystem.BuildTestServiceProvider(useStore: true);
            BeaconState state = TestState.PrepareTestState(testServiceProvider);

            IForkChoice forkChoice = testServiceProvider.GetService<IForkChoice>();
            IStore store = testServiceProvider.GetService<IStore>();
            await forkChoice.InitializeForkChoiceStoreAsync(store, state);

            // Act
            await RunOnTick(testServiceProvider, store, store.Time + 1, expectNewJustifiedCheckpoint: false);

            // Assert
        }

        [TestMethod]
        public async Task UpdateJustifiedSingle()
        {
            // Arrange
            IServiceProvider testServiceProvider = TestSystem.BuildTestServiceProvider(useStore: true);
            BeaconState state = TestState.PrepareTestState(testServiceProvider);

            IForkChoice forkChoice = testServiceProvider.GetService<IForkChoice>();
            IStore store = testServiceProvider.GetService<IStore>();
            await forkChoice.InitializeForkChoiceStoreAsync(store, state);

            TimeParameters timeParameters = testServiceProvider.GetService<IOptions<TimeParameters>>().Value;

            ulong secondsPerEpoch = timeParameters.SecondsPerSlot * (ulong)timeParameters.SlotsPerEpoch;
            Checkpoint checkpoint = new Checkpoint(
                store.JustifiedCheckpoint.Epoch + Epoch.One,
                new Root(Enumerable.Repeat((byte)0x55, 32).ToArray()));
            await store.SetBestJustifiedCheckpointAsync(checkpoint);

            // Act
            await RunOnTick(testServiceProvider, store, store.Time + secondsPerEpoch, expectNewJustifiedCheckpoint: true);

            // Assert
        }

        private async Task RunOnTick(IServiceProvider testServiceProvider, IStore store, ulong time, bool expectNewJustifiedCheckpoint)
        {
            IForkChoice forkChoice = testServiceProvider.GetService<IForkChoice>();

            Checkpoint previousJustifiedCheckpoint = store.JustifiedCheckpoint;

            await forkChoice.OnTickAsync(store, time);

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
