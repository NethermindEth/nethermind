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
using System.Collections.Generic;
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
using Nethermind.Core2.Store;
using Nethermind.Core2.Types;
using Shouldly;
namespace Nethermind.BeaconNode.Test.ForkTests
{
    [TestClass]
    public class OnAttestationTest
    {
        [TestMethod]
        public async Task OnAttestationCurrentEpoch()
        {
            // Arrange
            IServiceProvider testServiceProvider = TestSystem.BuildTestServiceProvider(useStore: true);
            BeaconState state = TestState.PrepareTestState(testServiceProvider);

            ChainConstants chainConstants = testServiceProvider.GetService<ChainConstants>();
            InitialValues initialValues = testServiceProvider.GetService<IOptions<InitialValues>>().Value;
            TimeParameters timeParameters = testServiceProvider.GetService<IOptions<TimeParameters>>().Value;
            IForkChoice forkChoice = testServiceProvider.GetService<IForkChoice>();

            // Initialization
            IStore store = testServiceProvider.GetService<IStore>();
            await forkChoice.InitializeForkChoiceStoreAsync(store, state);            
            ulong time = store.Time + timeParameters.SecondsPerSlot * 2;
            await forkChoice.OnTickAsync(store, time);

            BeaconBlock block = TestBlock.BuildEmptyBlockForNextSlot(testServiceProvider, state, BlsSignature.Zero);
            SignedBeaconBlock signedBlock = TestState.StateTransitionAndSignBlock(testServiceProvider, state, block);

            // Store block in store
            await forkChoice.OnBlockAsync(store, signedBlock);

            Attestation attestation = TestAttestation.GetValidAttestation(testServiceProvider, state, block.Slot, CommitteeIndex.None, signed: true);

            attestation.Data.Target.Epoch.ShouldBe(chainConstants.GenesisEpoch);

            IBeaconChainUtility beaconChainUtility = testServiceProvider.GetService<IBeaconChainUtility>();
            Slot currentSlot = ((ForkChoice)forkChoice).GetCurrentSlot(store);
            Epoch currentEpoch = beaconChainUtility.ComputeEpochAtSlot(currentSlot);
            currentEpoch.ShouldBe(chainConstants.GenesisEpoch);

            await RunOnAttestation(testServiceProvider, state, store, attestation, expectValid: true);
        }

        [TestMethod]
        public async Task OnAttestationPreviousEpoch()
        {
            // Arrange
            IServiceProvider testServiceProvider = TestSystem.BuildTestServiceProvider(useStore: true);
            BeaconState state = TestState.PrepareTestState(testServiceProvider);

            ChainConstants chainConstants = testServiceProvider.GetService<ChainConstants>();
            InitialValues initialValues = testServiceProvider.GetService<IOptions<InitialValues>>().Value;
            TimeParameters timeParameters = testServiceProvider.GetService<IOptions<TimeParameters>>().Value;
            IForkChoice forkChoice = testServiceProvider.GetService<IForkChoice>();

            // Initialization
            IStore store = testServiceProvider.GetService<IStore>();
            await forkChoice.InitializeForkChoiceStoreAsync(store, state);            
            ulong time = store.Time + timeParameters.SecondsPerSlot * (ulong)timeParameters.SlotsPerEpoch;
            await forkChoice.OnTickAsync(store, time);

            BeaconBlock block = TestBlock.BuildEmptyBlockForNextSlot(testServiceProvider, state, BlsSignature.Zero);
            SignedBeaconBlock signedBlock = TestState.StateTransitionAndSignBlock(testServiceProvider, state, block);

            // Store block in store
            await forkChoice.OnBlockAsync(store, signedBlock);

            Attestation attestation = TestAttestation.GetValidAttestation(testServiceProvider, state, block.Slot, CommitteeIndex.None, signed: true);

            attestation.Data.Target.Epoch.ShouldBe(chainConstants.GenesisEpoch);

            IBeaconChainUtility beaconChainUtility = testServiceProvider.GetService<IBeaconChainUtility>();
            Slot currentSlot = ((ForkChoice)forkChoice).GetCurrentSlot(store);
            Epoch currentEpoch = beaconChainUtility.ComputeEpochAtSlot(currentSlot);
            currentEpoch.ShouldBe(chainConstants.GenesisEpoch + Epoch.One);

            await RunOnAttestation(testServiceProvider, state, store, attestation, expectValid: true);
        }

        [TestMethod]
        public async Task OnAttestationPastEpoch()
        {
            // Arrange
            IServiceProvider testServiceProvider = TestSystem.BuildTestServiceProvider(useStore: true);
            BeaconState state = TestState.PrepareTestState(testServiceProvider);

            ChainConstants chainConstants = testServiceProvider.GetService<ChainConstants>();
            InitialValues initialValues = testServiceProvider.GetService<IOptions<InitialValues>>().Value;
            TimeParameters timeParameters = testServiceProvider.GetService<IOptions<TimeParameters>>().Value;
            IForkChoice forkChoice = testServiceProvider.GetService<IForkChoice>();

            // Initialization
            IStore store = testServiceProvider.GetService<IStore>();
            await forkChoice.InitializeForkChoiceStoreAsync(store, state);            

            // move time forward 2 epochs
            ulong time = store.Time + 2 * timeParameters.SecondsPerSlot * (ulong)timeParameters.SlotsPerEpoch;
            await forkChoice.OnTickAsync(store, time);

            // create and store block from 3 epochs ago
            BeaconBlock block = TestBlock.BuildEmptyBlockForNextSlot(testServiceProvider, state, BlsSignature.Zero);
            SignedBeaconBlock signedBlock = TestState.StateTransitionAndSignBlock(testServiceProvider, state, block);
            await forkChoice.OnBlockAsync(store, signedBlock);

            // create attestation for past block
            Attestation attestation = TestAttestation.GetValidAttestation(testServiceProvider, state, state.Slot, CommitteeIndex.None, signed: true);

            attestation.Data.Target.Epoch.ShouldBe(chainConstants.GenesisEpoch);

            IBeaconChainUtility beaconChainUtility = testServiceProvider.GetService<IBeaconChainUtility>();
            Slot currentSlot = ((ForkChoice)forkChoice).GetCurrentSlot(store);
            Epoch currentEpoch = beaconChainUtility.ComputeEpochAtSlot(currentSlot);
            currentEpoch.ShouldBe((Epoch)(chainConstants.GenesisEpoch + 2UL));

            await RunOnAttestation(testServiceProvider, state, store, attestation, expectValid: false);
        }

        private async Task RunOnAttestation(IServiceProvider testServiceProvider, BeaconState state, IStore store, Attestation attestation, bool expectValid)
        {
            MiscellaneousParameters miscellaneousParameters = testServiceProvider.GetService<IOptions<MiscellaneousParameters>>().Value;
            MaxOperationsPerBlock maxOperationsPerBlock = testServiceProvider.GetService<IOptions<MaxOperationsPerBlock>>().Value;

            IForkChoice forkChoice = testServiceProvider.GetService<IForkChoice>();

            if (!expectValid)
            {
                Should.Throw<Exception>(async () =>
                {
                    await forkChoice.OnAttestationAsync(store, attestation);
                });
                return;
            }

            BeaconStateAccessor beaconStateAccessor = testServiceProvider.GetService<BeaconStateAccessor>();
            IndexedAttestation indexedAttestation = beaconStateAccessor.GetIndexedAttestation(state, attestation);
            await forkChoice.OnAttestationAsync(store, attestation);

            IEnumerable<ValidatorIndex> attestingIndices = beaconStateAccessor.GetAttestingIndices(state, attestation.Data, attestation.AggregationBits);
            ValidatorIndex firstAttestingIndex = attestingIndices.First();
            LatestMessage latestMessage = (await store.GetLatestMessageAsync(firstAttestingIndex, true))!;

            latestMessage.Epoch.ShouldBe(attestation.Data.Target.Epoch);
            latestMessage.Root.ShouldBe(attestation.Data.BeaconBlockRoot);
        }
    }
}
