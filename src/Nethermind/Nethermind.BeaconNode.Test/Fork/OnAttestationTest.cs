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
using Nethermind.BeaconNode.Configuration;
using Nethermind.BeaconNode.Containers;
using Nethermind.BeaconNode.Storage;
using Nethermind.BeaconNode.Tests.Helpers;
using Nethermind.Core2.Types;
using Shouldly;

namespace Nethermind.BeaconNode.Tests.Fork
{
    [TestClass]
    public class OnAttestationTest
    {
        [TestMethod]
        public async Task OnAttestationCurrentEpoch()
        {
            // Arrange
            var testServiceProvider = TestSystem.BuildTestServiceProvider(useStore: true);
            var state = TestState.PrepareTestState(testServiceProvider);

            var initialValues = testServiceProvider.GetService<IOptions<InitialValues>>().Value;
            var timeParameters = testServiceProvider.GetService<IOptions<TimeParameters>>().Value;
            var forkChoice = testServiceProvider.GetService<ForkChoice>();

            // Initialization
            var store = forkChoice.GetGenesisStore(state);
            var time = store.Time + timeParameters.SecondsPerSlot * 2;
            await forkChoice.OnTickAsync(store, time);

            var block = TestBlock.BuildEmptyBlockForNextSlot(testServiceProvider, state, signed: true);
            TestState.StateTransitionAndSignBlock(testServiceProvider, state, block);

            // Store block in store
            await forkChoice.OnBlockAsync(store, block);

            var attestation = TestAttestation.GetValidAttestation(testServiceProvider, state, block.Slot, CommitteeIndex.None, signed: true);

            attestation.Data.Target.Epoch.ShouldBe(initialValues.GenesisEpoch);

            var beaconChainUtility = testServiceProvider.GetService<BeaconChainUtility>();
            var currentSlot = forkChoice.GetCurrentSlot(store);
            var currentEpoch = beaconChainUtility.ComputeEpochAtSlot(currentSlot);
            currentEpoch.ShouldBe(initialValues.GenesisEpoch);

            await RunOnAttestation(testServiceProvider, state, store, attestation, expectValid: true);
        }

        [TestMethod]
        public async Task OnAttestationPreviousEpoch()
        {
            // Arrange
            var testServiceProvider = TestSystem.BuildTestServiceProvider(useStore: true);
            var state = TestState.PrepareTestState(testServiceProvider);

            var initialValues = testServiceProvider.GetService<IOptions<InitialValues>>().Value;
            var timeParameters = testServiceProvider.GetService<IOptions<TimeParameters>>().Value;
            var forkChoice = testServiceProvider.GetService<ForkChoice>();

            // Initialization
            var store = forkChoice.GetGenesisStore(state);
            var time = store.Time + timeParameters.SecondsPerSlot * (ulong)timeParameters.SlotsPerEpoch;
            await forkChoice.OnTickAsync(store, time);

            var block = TestBlock.BuildEmptyBlockForNextSlot(testServiceProvider, state, signed: true);
            TestState.StateTransitionAndSignBlock(testServiceProvider, state, block);

            // Store block in store
            await forkChoice.OnBlockAsync(store, block);

            var attestation = TestAttestation.GetValidAttestation(testServiceProvider, state, block.Slot, CommitteeIndex.None, signed: true);

            attestation.Data.Target.Epoch.ShouldBe(initialValues.GenesisEpoch);

            var beaconChainUtility = testServiceProvider.GetService<BeaconChainUtility>();
            var currentSlot = forkChoice.GetCurrentSlot(store);
            var currentEpoch = beaconChainUtility.ComputeEpochAtSlot(currentSlot);
            currentEpoch.ShouldBe(initialValues.GenesisEpoch + Epoch.One);

            await RunOnAttestation(testServiceProvider, state, store, attestation, expectValid: true);
        }

        [TestMethod]
        public async Task OnAttestationPastEpoch()
        {
            // Arrange
            var testServiceProvider = TestSystem.BuildTestServiceProvider(useStore: true);
            var state = TestState.PrepareTestState(testServiceProvider);

            var initialValues = testServiceProvider.GetService<IOptions<InitialValues>>().Value;
            var timeParameters = testServiceProvider.GetService<IOptions<TimeParameters>>().Value;
            var forkChoice = testServiceProvider.GetService<ForkChoice>();

            // Initialization
            var store = forkChoice.GetGenesisStore(state);

            // move time forward 2 epochs
            var time = store.Time + 2 * timeParameters.SecondsPerSlot * (ulong)timeParameters.SlotsPerEpoch;
            await forkChoice.OnTickAsync(store, time);

            // create and store block from 3 epochs ago
            var block = TestBlock.BuildEmptyBlockForNextSlot(testServiceProvider, state, signed: true);
            TestState.StateTransitionAndSignBlock(testServiceProvider, state, block);
            await forkChoice.OnBlockAsync(store, block);

            // create attestation for past block
            var attestation = TestAttestation.GetValidAttestation(testServiceProvider, state, state.Slot, CommitteeIndex.None, signed: true);

            attestation.Data.Target.Epoch.ShouldBe(initialValues.GenesisEpoch);

            var beaconChainUtility = testServiceProvider.GetService<BeaconChainUtility>();
            var currentSlot = forkChoice.GetCurrentSlot(store);
            var currentEpoch = beaconChainUtility.ComputeEpochAtSlot(currentSlot);
            currentEpoch.ShouldBe((Epoch)(initialValues.GenesisEpoch + 2UL));

            await RunOnAttestation(testServiceProvider, state, store, attestation, expectValid: false);
        }

        private async Task RunOnAttestation(IServiceProvider testServiceProvider, BeaconState state, IStore store, Attestation attestation, bool expectValid)
        {
            var miscellaneousParameters = testServiceProvider.GetService<IOptions<MiscellaneousParameters>>().Value;
            var maxOperationsPerBlock = testServiceProvider.GetService<IOptions<MaxOperationsPerBlock>>().Value;

            var forkChoice = testServiceProvider.GetService<ForkChoice>();

            if (!expectValid)
            {
                Should.Throw<Exception>(async () =>
                {
                    await forkChoice.OnAttestationAsync(store, attestation);
                });
                return;
            }

            var beaconStateAccessor = testServiceProvider.GetService<BeaconStateAccessor>();
            var indexedAttestation = beaconStateAccessor.GetIndexedAttestation(state, attestation);
            await forkChoice.OnAttestationAsync(store, attestation);

            var attestingIndices = beaconStateAccessor.GetAttestingIndices(state, attestation.Data, attestation.AggregationBits);
            var firstAttestingIndex = attestingIndices.First();
            var latestMessage = (await store.GetLatestMessageAsync(firstAttestingIndex, true))!;

            latestMessage.Epoch.ShouldBe(attestation.Data.Target.Epoch);
            latestMessage.Root.ShouldBe(attestation.Data.BeaconBlockRoot);
        }
    }
}
