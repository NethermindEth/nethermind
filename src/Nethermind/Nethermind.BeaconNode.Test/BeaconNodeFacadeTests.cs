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
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nethermind.BeaconNode.Eth1Bridge;
using Nethermind.BeaconNode.Eth1Bridge.MockedStart;
using Nethermind.Core2.Configuration;
using Nethermind.BeaconNode.MockedStart;
using Nethermind.BeaconNode.Services;
using Nethermind.BeaconNode.Storage;
using Nethermind.BeaconNode.Test.Helpers;
using Nethermind.Core2;
using Nethermind.Core2.Api;
using Nethermind.Core2.Containers;
using Nethermind.Core2.Crypto;
using Nethermind.Core2.Types;
using NSubstitute;
using Shouldly;

namespace Nethermind.BeaconNode.Test
{
    [TestClass]
    public class BeaconNodeFacadeTests
    {
        [TestMethod]
        public async Task ShouldReturnDefaultForkVersion()
        {
            // Arrange
            IServiceCollection testServiceCollection = TestSystem.BuildTestServiceCollection(useStore: true);
            testServiceCollection.AddSingleton<IHostEnvironment>(Substitute.For<IHostEnvironment>());
            testServiceCollection.AddSingleton<IEth1DataProvider>(Substitute.For<IEth1DataProvider>());
            testServiceCollection.AddSingleton<IOperationPool>(Substitute.For<IOperationPool>());
            ServiceProvider testServiceProvider = testServiceCollection.BuildServiceProvider();
            BeaconState state = TestState.PrepareTestState(testServiceProvider);
            IForkChoice forkChoice = testServiceProvider.GetService<IForkChoice>();
            // Get genesis store initialise MemoryStoreProvider with the state
            IStore store = testServiceProvider.GetService<IStore>();
            await forkChoice.InitializeForkChoiceStoreAsync(store, state);            

            // Act
            IBeaconNodeApi beaconNode = testServiceProvider.GetService<IBeaconNodeApi>();
            beaconNode.ShouldBeOfType(typeof(BeaconNodeFacade));

            var forkResponse = await beaconNode.GetNodeForkAsync(CancellationToken.None);
            var fork = forkResponse.Content;

            // Assert
            fork.Epoch.ShouldBe(Epoch.Zero);
            fork.CurrentVersion.ShouldBe(new ForkVersion());
            fork.PreviousVersion.ShouldBe(new ForkVersion());
        }
        
        [TestMethod]
        public async Task TestValidators80DutiesForEpochZeroHaveExactlyOneProposerPerSlot()
        {
            // Arrange
            IServiceCollection testServiceCollection = TestSystem.BuildTestServiceCollection(useStore: true);
            testServiceCollection.AddSingleton<IHostEnvironment>(Substitute.For<IHostEnvironment>());
            testServiceCollection.AddSingleton<IEth1DataProvider>(Substitute.For<IEth1DataProvider>());
            testServiceCollection.AddSingleton<IOperationPool>(Substitute.For<IOperationPool>());
            ServiceProvider testServiceProvider = testServiceCollection.BuildServiceProvider();
            BeaconState state = TestState.PrepareTestState(testServiceProvider);
            IForkChoice forkChoice = testServiceProvider.GetService<IForkChoice>();
            // Get genesis store initialise MemoryStoreProvider with the state
            IStore store = testServiceProvider.GetService<IStore>();
            await forkChoice.InitializeForkChoiceStoreAsync(store, state);            
            
            TimeParameters timeParameters = testServiceProvider.GetService<IOptions<TimeParameters>>().Value;
            int numberOfValidators = state.Validators.Count;
            Console.WriteLine("Number of validators: {0}", numberOfValidators);
            BlsPublicKey[] publicKeys = TestKeys.PublicKeys(timeParameters).ToArray();
            byte[][] privateKeys = TestKeys.PrivateKeys(timeParameters).ToArray();
            for (int index = 0; index < numberOfValidators; index++)
            {
                Console.WriteLine("[{0}] priv:{1} pub:{2}", index, "0x" + BitConverter.ToString(privateKeys[index]).Replace("-", ""), publicKeys[index]);
            }
            
            // Act
            Epoch targetEpoch = new Epoch(0);
            var validatorPublicKeys = publicKeys.Take(numberOfValidators).ToList();
            IBeaconNodeApi beaconNode = testServiceProvider.GetService<IBeaconNodeApi>();
            beaconNode.ShouldBeOfType(typeof(BeaconNodeFacade));

            int validatorDutyIndex = 0;
            List<ValidatorDuty> validatorDuties = new List<ValidatorDuty>();
            var validatorDutiesResponse =
                await beaconNode.ValidatorDutiesAsync(validatorPublicKeys, targetEpoch, CancellationToken.None);
            foreach (ValidatorDuty validatorDuty in validatorDutiesResponse.Content)
            {
                validatorDuties.Add(validatorDuty);
                Console.WriteLine("Index [{0}], Epoch {1}, Validator {2}, : attestation slot {3}, shard {4}, proposal slot {5}",
                    validatorDutyIndex, targetEpoch, validatorDuty.ValidatorPublicKey, validatorDuty.AttestationSlot,
                    validatorDuty.AttestationIndex, validatorDuty.BlockProposalSlot);
                validatorDutyIndex++;
            }

            Console.WriteLine();
            Console.WriteLine("** ValidatorDuty summary");
            foreach (ValidatorDuty validatorDuty in validatorDuties)
            {
                Console.WriteLine("Index [{0}], Epoch {1}, Validator {2}, : attestation slot {3}, shard {4}, proposal slot {5}",
                    validatorDutyIndex, targetEpoch, validatorDuty.ValidatorPublicKey, validatorDuty.AttestationSlot,
                    validatorDuty.AttestationIndex, validatorDuty.BlockProposalSlot);
            }

            // Assert
            Dictionary<Slot, IGrouping<Slot, ValidatorDuty>> groupsByProposalSlot = validatorDuties
                .Where(x => x.BlockProposalSlot.HasValue)
                .GroupBy(x => x.BlockProposalSlot!.Value)
                .ToDictionary(x => x.Key, x => x);
            groupsByProposalSlot[new Slot(0)].Count().ShouldBe(1);
            groupsByProposalSlot[new Slot(1)].Count().ShouldBe(1);
            groupsByProposalSlot[new Slot(2)].Count().ShouldBe(1);
            groupsByProposalSlot[new Slot(3)].Count().ShouldBe(1);
            groupsByProposalSlot[new Slot(4)].Count().ShouldBe(1);
            groupsByProposalSlot[new Slot(5)].Count().ShouldBe(1);
            groupsByProposalSlot[new Slot(6)].Count().ShouldBe(1);
            //groupsByProposalSlot[new Slot(7)].Count().ShouldBe(1);
            validatorDuties.Count(x => !x.BlockProposalSlot.HasValue).ShouldBe(numberOfValidators - 7);
        }
        
        [TestMethod]
        public async Task TestValidators80DutiesForEpochOneHaveExactlyOneProposerPerSlot()
        {
            // Arrange
            IServiceCollection testServiceCollection = TestSystem.BuildTestServiceCollection(useStore: true);
            testServiceCollection.AddSingleton<IHostEnvironment>(Substitute.For<IHostEnvironment>());
            testServiceCollection.AddSingleton<IEth1DataProvider>(Substitute.For<IEth1DataProvider>());
            testServiceCollection.AddSingleton<IOperationPool>(Substitute.For<IOperationPool>());
            ServiceProvider testServiceProvider = testServiceCollection.BuildServiceProvider();
            BeaconState state = TestState.PrepareTestState(testServiceProvider);
            IForkChoice forkChoice = testServiceProvider.GetService<IForkChoice>();
            // Get genesis store initialise MemoryStoreProvider with the state
            IStore store = testServiceProvider.GetService<IStore>();
            await forkChoice.InitializeForkChoiceStoreAsync(store, state);            
            
            TimeParameters timeParameters = testServiceProvider.GetService<IOptions<TimeParameters>>().Value;
            int numberOfValidators = state.Validators.Count;
            Console.WriteLine("Number of validators: {0}", numberOfValidators);
            BlsPublicKey[] publicKeys = TestKeys.PublicKeys(timeParameters).ToArray();
            
            // Act
            Epoch targetEpoch = new Epoch(1);
            var validatorPublicKeys = publicKeys.Take(numberOfValidators).ToList();
            IBeaconNodeApi beaconNode = testServiceProvider.GetService<IBeaconNodeApi>();
            beaconNode.ShouldBeOfType(typeof(BeaconNodeFacade));
            
            int validatorDutyIndex = 0;
            List<ValidatorDuty> validatorDuties = new List<ValidatorDuty>();
            var validatorDutiesResponse =
                await beaconNode.ValidatorDutiesAsync(validatorPublicKeys, targetEpoch, CancellationToken.None);
            foreach (ValidatorDuty validatorDuty in validatorDutiesResponse.Content)
            {
                validatorDuties.Add(validatorDuty);
                Console.WriteLine("Index [{0}], Epoch {1}, Validator {2}, : attestation slot {3}, shard {4}, proposal slot {5}",
                    validatorDutyIndex, targetEpoch, validatorDuty.ValidatorPublicKey, validatorDuty.AttestationSlot,
                    validatorDuty.AttestationIndex, validatorDuty.BlockProposalSlot);
                validatorDutyIndex++;
            }

            Console.WriteLine();
            Console.WriteLine("** ValidatorDuty summary");
            foreach (ValidatorDuty validatorDuty in validatorDuties)
            {
                Console.WriteLine("Index [{0}], Epoch {1}, Validator {2}, : attestation slot {3}, shard {4}, proposal slot {5}",
                    validatorDutyIndex, targetEpoch, validatorDuty.ValidatorPublicKey, validatorDuty.AttestationSlot,
                    validatorDuty.AttestationIndex, validatorDuty.BlockProposalSlot);
            }
            
            // Assert
            Dictionary<Slot, IGrouping<Slot, ValidatorDuty>> groupsByProposalSlot = validatorDuties
                .Where(x => x.BlockProposalSlot.HasValue)
                .GroupBy(x => x.BlockProposalSlot!.Value)
                .ToDictionary(x => x.Key, x => x);
            groupsByProposalSlot[new Slot(8)].Count().ShouldBe(1);
            groupsByProposalSlot[new Slot(9)].Count().ShouldBe(1);
            groupsByProposalSlot[new Slot(10)].Count().ShouldBe(1);
            groupsByProposalSlot[new Slot(11)].Count().ShouldBe(1);
            groupsByProposalSlot[new Slot(12)].Count().ShouldBe(1);
            groupsByProposalSlot[new Slot(13)].Count().ShouldBe(1);
            groupsByProposalSlot[new Slot(14)].Count().ShouldBe(1);
            //groupsByProposalSlot[new Slot(15)].Count().ShouldBe(1);
            validatorDuties.Count(x => !x.BlockProposalSlot.HasValue).ShouldBe(numberOfValidators - 7);
        }
        
        [TestMethod]
        public async Task QuickStart64DutiesForEpochZeroHaveExactlyOneProposerPerSlot()
        {
            // Arrange
            int numberOfValidators = 64;
            int genesisTime = 1578009600;
            IServiceCollection testServiceCollection = TestSystem.BuildTestServiceCollection(useStore: true);
            IConfigurationRoot configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string>
                {
                    ["QuickStart:ValidatorCount"] = $"{numberOfValidators}",
                    ["QuickStart:GenesisTime"] = $"{genesisTime}"
                })
                .Build();
            testServiceCollection.AddBeaconNodeEth1Bridge(configuration);
            testServiceCollection.AddBeaconNodeQuickStart(configuration);
            testServiceCollection.AddSingleton<IHostEnvironment>(Substitute.For<IHostEnvironment>());
            ServiceProvider testServiceProvider = testServiceCollection.BuildServiceProvider();
            
            Eth1BridgeWorker eth1BridgeWorker =
                testServiceProvider.GetServices<IHostedService>().OfType<Eth1BridgeWorker>().First();
            await eth1BridgeWorker.ExecuteEth1GenesisAsync(CancellationToken.None);

            IStore store = testServiceProvider.GetService<IStore>();
            BeaconState state = await store!.GetBlockStateAsync(store.FinalizedCheckpoint.Root);
            
            // Act
            Epoch targetEpoch = new Epoch(0);
            var validatorPublicKeys = state!.Validators.Select(x => x.PublicKey).ToList();
            IBeaconNodeApi beaconNode = testServiceProvider.GetService<IBeaconNodeApi>();
            beaconNode.ShouldBeOfType(typeof(BeaconNodeFacade));
            
            int validatorDutyIndex = 0;
            List<ValidatorDuty> validatorDuties = new List<ValidatorDuty>();
            var validatorDutiesResponse =
                await beaconNode.ValidatorDutiesAsync(validatorPublicKeys, targetEpoch, CancellationToken.None);
            foreach (ValidatorDuty validatorDuty in validatorDutiesResponse.Content)
            {
                validatorDuties.Add(validatorDuty);
                Console.WriteLine("Index [{0}], Epoch {1}, Validator {2}, : attestation slot {3}, shard {4}, proposal slot {5}",
                    validatorDutyIndex, targetEpoch, validatorDuty.ValidatorPublicKey, validatorDuty.AttestationSlot,
                    validatorDuty.AttestationIndex, validatorDuty.BlockProposalSlot);
                validatorDutyIndex++;
            }

            Console.WriteLine();
            Console.WriteLine("** ValidatorDuty summary");
            foreach (ValidatorDuty validatorDuty in validatorDuties)
            {
                Console.WriteLine("Index [{0}], Epoch {1}, Validator {2}, : attestation slot {3}, shard {4}, proposal slot {5}",
                    validatorDutyIndex, targetEpoch, validatorDuty.ValidatorPublicKey, validatorDuty.AttestationSlot,
                    validatorDuty.AttestationIndex, validatorDuty.BlockProposalSlot);
            }
            
            // Assert
            Dictionary<Slot, IGrouping<Slot, ValidatorDuty>> groupsByProposalSlot = validatorDuties
                .Where(x => x.BlockProposalSlot.HasValue)
                .GroupBy(x => x.BlockProposalSlot!.Value)
                .ToDictionary(x => x.Key, x => x);
            groupsByProposalSlot[new Slot(0)].Count().ShouldBe(1);
            groupsByProposalSlot[new Slot(1)].Count().ShouldBe(1);
            groupsByProposalSlot[new Slot(2)].Count().ShouldBe(1);
            groupsByProposalSlot[new Slot(3)].Count().ShouldBe(1);
            groupsByProposalSlot[new Slot(4)].Count().ShouldBe(1);
            groupsByProposalSlot[new Slot(5)].Count().ShouldBe(1);
            groupsByProposalSlot[new Slot(6)].Count().ShouldBe(1);
            //groupsByProposalSlot[new Slot(7)].Count().ShouldBe(1);
            //groupsByProposalSlot[Slot.None].Count().ShouldBe(numberOfValidators - 8);
        }
        
        [TestMethod]
        public async Task ShouldGetStatusWhenSyncing()
        {
            // Arrange
            Slot starting = Slot.One;
            Slot current = new Slot(5);
            Slot highest = new Slot(10);
            INetworkPeering mockNetworkPeering = Substitute.For<INetworkPeering>();
            mockNetworkPeering.HighestPeerSlot.Returns(highest);
            mockNetworkPeering.SyncStartingSlot.Returns(starting);
            IStore mockStore = Substitute.For<IStore>();
            Root root = new Root(Enumerable.Repeat((byte) 0x12, 32).ToArray());
            Checkpoint checkpoint = new Checkpoint(Epoch.Zero, root);
            SignedBeaconBlock signedBlock =
                new SignedBeaconBlock(new BeaconBlock(current, Root.Zero, Root.Zero, BeaconBlockBody.Zero),
                    BlsSignature.Zero);
            var state = TestState.Create(slot: current, finalizedCheckpoint: checkpoint);
            mockStore.GetHeadAsync().Returns(root);
            mockStore.GetSignedBlockAsync(root).Returns(signedBlock);
            mockStore.GetBlockStateAsync(root).Returns(state);
            mockStore.IsInitialized.Returns(true);
            mockStore.JustifiedCheckpoint.Returns(checkpoint);
            
            IServiceCollection testServiceCollection = TestSystem.BuildTestServiceCollection(useStore: true);
            testServiceCollection.AddSingleton(mockNetworkPeering);
            testServiceCollection.AddSingleton(mockStore);
            testServiceCollection.AddSingleton<IHostEnvironment>(Substitute.For<IHostEnvironment>());
            testServiceCollection.AddSingleton<IEth1DataProvider>(Substitute.For<IEth1DataProvider>());
            testServiceCollection.AddSingleton<IOperationPool>(Substitute.For<IOperationPool>());
            ServiceProvider testServiceProvider = testServiceCollection.BuildServiceProvider();

            // Act
            IBeaconNodeApi beaconNode = testServiceProvider.GetService<IBeaconNodeApi>();
            beaconNode.ShouldBeOfType(typeof(BeaconNodeFacade));

            var syncingResponse = await beaconNode.GetSyncingAsync(CancellationToken.None);
            var syncing = syncingResponse.Content;

            // Assert
            syncing.IsSyncing.ShouldBeTrue();
            syncing.SyncStatus!.StartingSlot.ShouldBe(Slot.One);
            syncing.SyncStatus.CurrentSlot.ShouldBe(new Slot(5));
            syncing.SyncStatus.HighestSlot.ShouldBe(new Slot(10));
        }
        
        [TestMethod]
        public async Task ShouldGetStatusWhenSyncComplete()
        {
            // Arrange
            Slot starting = Slot.One;
            Slot current = new Slot(11);
            Slot highest = new Slot(11);
            INetworkPeering mockNetworkPeering = Substitute.For<INetworkPeering>();
            mockNetworkPeering.HighestPeerSlot.Returns(highest);
            mockNetworkPeering.SyncStartingSlot.Returns(starting);
            IStore mockStore = Substitute.For<IStore>();
            Root root = new Root(Enumerable.Repeat((byte) 0x12, 32).ToArray());
            Checkpoint checkpoint = new Checkpoint(Epoch.Zero, root);
            SignedBeaconBlock signedBlock =
                new SignedBeaconBlock(new BeaconBlock(current, Root.Zero, Root.Zero, BeaconBlockBody.Zero),
                    BlsSignature.Zero);
            BeaconState state = TestState.Create(slot: current, finalizedCheckpoint: checkpoint);
            mockStore.GetHeadAsync().Returns(root);
            mockStore.GetSignedBlockAsync(root).Returns(signedBlock);
            mockStore.GetBlockStateAsync(root).Returns(state);
            mockStore.IsInitialized.Returns(true);
            mockStore.JustifiedCheckpoint.Returns(checkpoint);
            
            IServiceCollection testServiceCollection = TestSystem.BuildTestServiceCollection(useStore: true);
            testServiceCollection.AddSingleton(mockNetworkPeering);
            testServiceCollection.AddSingleton(mockStore);
            testServiceCollection.AddSingleton<IHostEnvironment>(Substitute.For<IHostEnvironment>());
            testServiceCollection.AddSingleton<IEth1DataProvider>(Substitute.For<IEth1DataProvider>());
            testServiceCollection.AddSingleton<IOperationPool>(Substitute.For<IOperationPool>());
            ServiceProvider testServiceProvider = testServiceCollection.BuildServiceProvider();

            // Act
            IBeaconNodeApi beaconNode = testServiceProvider.GetService<IBeaconNodeApi>();
            beaconNode.ShouldBeOfType(typeof(BeaconNodeFacade));

            ApiResponse<Syncing> syncingResponse = await beaconNode.GetSyncingAsync(CancellationToken.None);
            Syncing syncing = syncingResponse.Content;

            // Assert
            syncing.IsSyncing.ShouldBeFalse();
            syncing.SyncStatus!.StartingSlot.ShouldBe(Slot.One);
            syncing.SyncStatus.CurrentSlot.ShouldBe(new Slot(11));
            syncing.SyncStatus.HighestSlot.ShouldBe(new Slot(11));
        }
    }
}