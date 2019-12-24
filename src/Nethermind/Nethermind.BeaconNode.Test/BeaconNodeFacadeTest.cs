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
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nethermind.BeaconNode.Configuration;
using Nethermind.BeaconNode.Containers;
using Nethermind.BeaconNode.MockedStart;
using Nethermind.BeaconNode.Services;
using Nethermind.BeaconNode.Storage;
using Nethermind.BeaconNode.Tests.Helpers;
using Nethermind.Core2.Crypto;
using Nethermind.Core2.Types;
using NSubstitute;
using Shouldly;

namespace Nethermind.BeaconNode.Tests
{
    [TestClass]
    public class BeaconNodeFacadeTest
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
            ForkChoice forkChoice = testServiceProvider.GetService<ForkChoice>();
            // Get genesis store initialise MemoryStoreProvider with the state
            _ = forkChoice.GetGenesisStore(state);            

            // Act
            IBeaconNodeApi beaconNode = testServiceProvider.GetService<IBeaconNodeApi>();
            beaconNode.ShouldBeOfType(typeof(BeaconNodeFacade));

            Core2.Containers.Fork fork = await beaconNode.GetNodeForkAsync();

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
            ForkChoice forkChoice = testServiceProvider.GetService<ForkChoice>();
            // Get genesis store initialise MemoryStoreProvider with the state
            _ = forkChoice.GetGenesisStore(state);            
            
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
            IEnumerable<BlsPublicKey> validatorPublicKeys = publicKeys.Take(numberOfValidators);
            IBeaconNodeApi beaconNode = testServiceProvider.GetService<IBeaconNodeApi>();
            beaconNode.ShouldBeOfType(typeof(BeaconNodeFacade));

            int validatorDutyIndex = 0;
            List<ValidatorDuty> validatorDuties = new List<ValidatorDuty>();
            await foreach (ValidatorDuty validatorDuty in beaconNode.ValidatorDutiesAsync(validatorPublicKeys, targetEpoch))
            {
                validatorDuties.Add(validatorDuty);
                Console.WriteLine("Index [{0}], Epoch {1}, Validator {2}, : attestation slot {3}, shard {4}, proposal slot {5}",
                    validatorDutyIndex, targetEpoch, validatorDuty.ValidatorPublicKey, validatorDuty.AttestationSlot,
                    (ulong) validatorDuty.AttestationShard, validatorDuty.BlockProposalSlot);
                validatorDutyIndex++;
            }
            
            // Assert
            Dictionary<Slot, IGrouping<Slot, ValidatorDuty>> groupsByProposalSlot = validatorDuties
                .GroupBy(x => x.BlockProposalSlot)
                .ToDictionary(x => x.Key, x => x);
            groupsByProposalSlot[new Slot(0)].Count().ShouldBe(1);
            groupsByProposalSlot[new Slot(1)].Count().ShouldBe(1);
            groupsByProposalSlot[new Slot(2)].Count().ShouldBe(1);
            groupsByProposalSlot[new Slot(3)].Count().ShouldBe(1);
            groupsByProposalSlot[new Slot(4)].Count().ShouldBe(1);
            groupsByProposalSlot[new Slot(5)].Count().ShouldBe(1);
            //groupsByProposalSlot[new Slot(6)].Count().ShouldBe(1);
            groupsByProposalSlot[new Slot(7)].Count().ShouldBe(1);
            //groupsByProposalSlot[Slot.None].Count().ShouldBe(numberOfValidators - 7);
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
            ForkChoice forkChoice = testServiceProvider.GetService<ForkChoice>();
            // Get genesis store initialise MemoryStoreProvider with the state
            _ = forkChoice.GetGenesisStore(state);            
            
            TimeParameters timeParameters = testServiceProvider.GetService<IOptions<TimeParameters>>().Value;
            int numberOfValidators = state.Validators.Count;
            Console.WriteLine("Number of validators: {0}", numberOfValidators);
            BlsPublicKey[] publicKeys = TestKeys.PublicKeys(timeParameters).ToArray();
            
            // Act
            Epoch targetEpoch = new Epoch(1);
            IEnumerable<BlsPublicKey> validatorPublicKeys = publicKeys.Take(numberOfValidators);
            IBeaconNodeApi beaconNode = testServiceProvider.GetService<IBeaconNodeApi>();
            beaconNode.ShouldBeOfType(typeof(BeaconNodeFacade));
            
            int validatorDutyIndex = 0;
            List<ValidatorDuty> validatorDuties = new List<ValidatorDuty>();
            await foreach (ValidatorDuty validatorDuty in beaconNode.ValidatorDutiesAsync(validatorPublicKeys, targetEpoch))
            {
                validatorDuties.Add(validatorDuty);
                Console.WriteLine("Index [{0}], Epoch {1}, Validator {2}, : attestation slot {3}, shard {4}, proposal slot {5}",
                    validatorDutyIndex, targetEpoch, validatorDuty.ValidatorPublicKey, validatorDuty.AttestationSlot,
                    (ulong) validatorDuty.AttestationShard, validatorDuty.BlockProposalSlot);
                validatorDutyIndex++;
            }
            
            // Assert
            Dictionary<Slot, IGrouping<Slot, ValidatorDuty>> groupsByProposalSlot = validatorDuties
                .GroupBy(x => x.BlockProposalSlot)
                .ToDictionary(x => x.Key, x => x);
            groupsByProposalSlot[new Slot(8)].Count().ShouldBe(1);
            groupsByProposalSlot[new Slot(9)].Count().ShouldBe(1);
            groupsByProposalSlot[new Slot(10)].Count().ShouldBe(1);
            groupsByProposalSlot[new Slot(11)].Count().ShouldBe(1);
            //groupsByProposalSlot[new Slot(12)].Count().ShouldBe(1);
            groupsByProposalSlot[new Slot(13)].Count().ShouldBe(1);
            groupsByProposalSlot[new Slot(14)].Count().ShouldBe(1);
            groupsByProposalSlot[new Slot(15)].Count().ShouldBe(1);
            //groupsByProposalSlot[Slot.None].Count().ShouldBe(numberOfValidators - 8);
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
            testServiceCollection.AddQuickStart(configuration);
            testServiceCollection.AddSingleton<IHostEnvironment>(Substitute.For<IHostEnvironment>());
            ServiceProvider testServiceProvider = testServiceCollection.BuildServiceProvider();
            
            INodeStart quickStart = testServiceProvider.GetService<INodeStart>();
            await quickStart.InitializeNodeAsync();

            IStoreProvider storeProvider = testServiceProvider.GetService<IStoreProvider>();
            storeProvider.TryGetStore(out IStore? store).ShouldBeTrue();
            BeaconState state = await store!.GetBlockStateAsync(store!.FinalizedCheckpoint.Root);
            
            // Act
            Epoch targetEpoch = new Epoch(0);
            IEnumerable<BlsPublicKey> validatorPublicKeys = state!.Validators.Select(x => x.PublicKey);
            IBeaconNodeApi beaconNode = testServiceProvider.GetService<IBeaconNodeApi>();
            beaconNode.ShouldBeOfType(typeof(BeaconNodeFacade));
            
            int validatorDutyIndex = 0;
            List<ValidatorDuty> validatorDuties = new List<ValidatorDuty>();
            await foreach (ValidatorDuty validatorDuty in beaconNode.ValidatorDutiesAsync(validatorPublicKeys, targetEpoch))
            {
                validatorDuties.Add(validatorDuty);
                Console.WriteLine("Index [{0}], Epoch {1}, Validator {2}, : attestation slot {3}, shard {4}, proposal slot {5}",
                    validatorDutyIndex, targetEpoch, validatorDuty.ValidatorPublicKey, validatorDuty.AttestationSlot,
                    (ulong) validatorDuty.AttestationShard, validatorDuty.BlockProposalSlot);
                validatorDutyIndex++;
            }
            
            // Assert
            Dictionary<Slot, IGrouping<Slot, ValidatorDuty>> groupsByProposalSlot = validatorDuties
                .GroupBy(x => x.BlockProposalSlot)
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
    }
}