//  Copyright (c) 2018 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

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
