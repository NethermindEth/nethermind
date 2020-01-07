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
using System.IO;
using System.Linq;
using System.Text.Json;
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
using Nethermind.Core2.Json;
using Nethermind.Core2.Types;
using Shouldly;
namespace Nethermind.BeaconNode.Test.Fork
{
    [TestClass]
    public class GetHeadTest
    {
        [TestMethod]
        public async Task GenesisHead()
        {
            // Arrange
            IServiceProvider testServiceProvider = TestSystem.BuildTestServiceProvider(useStore: true);
            BeaconState state = TestState.PrepareTestState(testServiceProvider);

            MiscellaneousParameters miscellaneousParameters = testServiceProvider.GetService<IOptions<MiscellaneousParameters>>().Value;
            TimeParameters timeParameters = testServiceProvider.GetService<IOptions<TimeParameters>>().Value;
            StateListLengths stateListLengths = testServiceProvider.GetService<IOptions<StateListLengths>>().Value;
            MaxOperationsPerBlock maxOperationsPerBlock = testServiceProvider.GetService<IOptions<MaxOperationsPerBlock>>().Value;

            JsonSerializerOptions options = new System.Text.Json.JsonSerializerOptions { WriteIndented = true };
            options.ConfigureNethermindCore2();
            string debugState = System.Text.Json.JsonSerializer.Serialize(state, options);
            
            // Initialization
            ICryptographyService cryptographyService = testServiceProvider.GetService<ICryptographyService>();
            ForkChoice forkChoice = testServiceProvider.GetService<ForkChoice>();
            IStore store = forkChoice.GetGenesisStore(state);

            // Act
            Hash32 headRoot = await forkChoice.GetHeadAsync(store);

            // Assert
            Hash32 stateRoot = cryptographyService.HashTreeRoot(state);
            BeaconBlock genesisBlock = new BeaconBlock(stateRoot);
            Hash32 expectedRoot = cryptographyService.SigningRoot(genesisBlock);
            
            headRoot.ShouldBe(expectedRoot);
        }

        [TestMethod]
        public async Task ChainNoAttestations()
        {
            // Arrange
            IServiceProvider testServiceProvider = TestSystem.BuildTestServiceProvider(useStore: true);
            BeaconState state = TestState.PrepareTestState(testServiceProvider);

            MiscellaneousParameters miscellaneousParameters = testServiceProvider.GetService<IOptions<MiscellaneousParameters>>().Value;
            MaxOperationsPerBlock maxOperationsPerBlock = testServiceProvider.GetService<IOptions<MaxOperationsPerBlock>>().Value;

            // Initialization
            ICryptographyService cryptographyService = testServiceProvider.GetService<ICryptographyService>();
            ForkChoice forkChoice = testServiceProvider.GetService<ForkChoice>();
            IStore store = forkChoice.GetGenesisStore(state);

            // On receiving a block of `GENESIS_SLOT + 1` slot
            BeaconBlock block1 = TestBlock.BuildEmptyBlockForNextSlot(testServiceProvider, state, signed: true);
            TestState.StateTransitionAndSignBlock(testServiceProvider, state, block1);
            await AddBlockToStore(testServiceProvider, store, block1);

            // On receiving a block of next epoch
            BeaconBlock block2 = TestBlock.BuildEmptyBlockForNextSlot(testServiceProvider, state, signed: true);
            TestState.StateTransitionAndSignBlock(testServiceProvider, state, block2);
            await AddBlockToStore(testServiceProvider, store, block2);

            // Act
            Hash32 headRoot = await forkChoice.GetHeadAsync(store);

            // Assert
            Hash32 expectedRoot = cryptographyService.SigningRoot(block2);
            headRoot.ShouldBe(expectedRoot);
        }

        [TestMethod]
        public async Task SplitTieBreakerNoAttestations()
        {
            // Arrange
            IServiceProvider testServiceProvider = TestSystem.BuildTestServiceProvider(useStore: true);
            BeaconState state = TestState.PrepareTestState(testServiceProvider);

            MiscellaneousParameters miscellaneousParameters = testServiceProvider.GetService<IOptions<MiscellaneousParameters>>().Value;
            MaxOperationsPerBlock maxOperationsPerBlock = testServiceProvider.GetService<IOptions<MaxOperationsPerBlock>>().Value;

            // Initialization
            ICryptographyService cryptographyService = testServiceProvider.GetService<ICryptographyService>();
            ForkChoice forkChoice = testServiceProvider.GetService<ForkChoice>();
            IStore store = forkChoice.GetGenesisStore(state);
            BeaconState genesisState = BeaconState.Clone(state);

            // block at slot 1
            BeaconState block1State = BeaconState.Clone(genesisState);
            BeaconBlock block1 = TestBlock.BuildEmptyBlockForNextSlot(testServiceProvider, block1State, signed: true);
            TestState.StateTransitionAndSignBlock(testServiceProvider, block1State, block1);
            await AddBlockToStore(testServiceProvider, store, block1);
            Hash32 block1Root = cryptographyService.SigningRoot(block1);

            // build short tree
            BeaconState block2State = BeaconState.Clone(genesisState);
            BeaconBlock block2 = TestBlock.BuildEmptyBlockForNextSlot(testServiceProvider, block2State, signed: true);
            block2.Body.SetGraffiti(new Bytes32(Enumerable.Repeat((byte)0x42, 32).ToArray()));
            TestBlock.SignBlock(testServiceProvider, block2State, block2, ValidatorIndex.None);
            TestState.StateTransitionAndSignBlock(testServiceProvider, block2State, block2);
            await AddBlockToStore(testServiceProvider, store, block2);
            Hash32 block2Root = cryptographyService.SigningRoot(block2);

            // Act
            Hash32 headRoot = await forkChoice.GetHeadAsync(store);

            // Assert
            Console.WriteLine("block1 {0}", block1Root);
            Console.WriteLine("block2 {0}", block2Root);
            Hash32 highestRoot = block1Root.CompareTo(block2Root) > 0 ? block1Root : block2Root;
            Console.WriteLine("highest {0}", highestRoot);
            headRoot.ShouldBe(highestRoot);
        }

        [TestMethod]
        public async Task ShorterChainButHeavierWeight()
        {
            // Arrange
            IServiceProvider testServiceProvider = TestSystem.BuildTestServiceProvider(useStore: true);
            BeaconState state = TestState.PrepareTestState(testServiceProvider);

            MiscellaneousParameters miscellaneousParameters = testServiceProvider.GetService<IOptions<MiscellaneousParameters>>().Value;
            MaxOperationsPerBlock maxOperationsPerBlock = testServiceProvider.GetService<IOptions<MaxOperationsPerBlock>>().Value;

            // Initialization
            ICryptographyService cryptographyService = testServiceProvider.GetService<ICryptographyService>();
            ForkChoice forkChoice = testServiceProvider.GetService<ForkChoice>();
            IStore store = forkChoice.GetGenesisStore(state);
            BeaconState genesisState = BeaconState.Clone(state);

            // build longer tree
            Hash32 longRoot = default;
            BeaconState longState = BeaconState.Clone(genesisState);
            for (int i = 0; i < 3; i++)
            {
                BeaconBlock longBlock = TestBlock.BuildEmptyBlockForNextSlot(testServiceProvider, longState, signed: true);
                TestState.StateTransitionAndSignBlock(testServiceProvider, longState, longBlock);
                await AddBlockToStore(testServiceProvider, store, longBlock);
                if (i == 2)
                {
                    longRoot = cryptographyService.SigningRoot(longBlock);
                }
            }

            // build short tree
            BeaconState shortState = BeaconState.Clone(genesisState);
            BeaconBlock shortBlock = TestBlock.BuildEmptyBlockForNextSlot(testServiceProvider, shortState, signed: true);
            shortBlock.Body.SetGraffiti(new Bytes32(Enumerable.Repeat((byte)0x42, 32).ToArray()));
            TestBlock.SignBlock(testServiceProvider, shortState, shortBlock, ValidatorIndex.None);
            TestState.StateTransitionAndSignBlock(testServiceProvider, shortState, shortBlock);
            await AddBlockToStore(testServiceProvider, store, shortBlock);

            Attestation shortAttestation = TestAttestation.GetValidAttestation(testServiceProvider, shortState, shortBlock.Slot, CommitteeIndex.None, signed: true);
            await AddAttestationToStore(testServiceProvider, store, shortAttestation);

            // Act
            Hash32 headRoot = await forkChoice.GetHeadAsync(store);

            // Assert
            Hash32 expectedRoot = cryptographyService.SigningRoot(shortBlock);
            headRoot.ShouldBe(expectedRoot);
            headRoot.ShouldNotBe(longRoot);
        }

        private async Task AddAttestationToStore(IServiceProvider testServiceProvider, IStore store, Attestation attestation)
        {
            TimeParameters timeParameters = testServiceProvider.GetService<IOptions<TimeParameters>>().Value;
            MiscellaneousParameters miscellaneousParameters = testServiceProvider.GetService<IOptions<MiscellaneousParameters>>().Value;
            MaxOperationsPerBlock maxOperationsPerBlock = testServiceProvider.GetService<IOptions<MaxOperationsPerBlock>>().Value;
            ICryptographyService cryptographyService = testServiceProvider.GetService<ICryptographyService>();
            ForkChoice forkChoice = testServiceProvider.GetService<ForkChoice>();

            BeaconBlock parentBlock = await store.GetBlockAsync(attestation.Data.BeaconBlockRoot);

            Hash32 parentSigningRoot = cryptographyService.SigningRoot(parentBlock);
            BeaconState preState = await store.GetBlockStateAsync(parentSigningRoot);
            
            ulong blockTime = preState.GenesisTime + (ulong)parentBlock.Slot * timeParameters.SecondsPerSlot;
            ulong nextEpochTime = blockTime + (ulong)timeParameters.SlotsPerEpoch * timeParameters.SecondsPerSlot;

            if (store.Time < blockTime)
            {
                await forkChoice.OnTickAsync(store, blockTime);
            }

            await forkChoice.OnAttestationAsync(store, attestation);
        }

        private async Task AddBlockToStore(IServiceProvider testServiceProvider, IStore store, BeaconBlock block)
        {
            TimeParameters timeParameters = testServiceProvider.GetService<IOptions<TimeParameters>>().Value;
            ForkChoice forkChoice = testServiceProvider.GetService<ForkChoice>();

            BeaconState preState = await store.GetBlockStateAsync(block.ParentRoot);
            
            ulong blockTime = preState!.GenesisTime + (ulong)block.Slot * timeParameters.SecondsPerSlot;

            if (store.Time < blockTime)
            {
                await forkChoice.OnTickAsync(store, blockTime);
            }

            await forkChoice.OnBlockAsync(store, block);
        }
    }
}
