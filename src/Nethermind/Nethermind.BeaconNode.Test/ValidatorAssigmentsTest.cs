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
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nethermind.Core2.Configuration;
using Nethermind.BeaconNode.Storage;
using Nethermind.BeaconNode.Test.Helpers;
using Nethermind.Core2;
using Nethermind.Core2.Containers;
using Nethermind.Core2.Crypto;
using Nethermind.Core2.Types;
using NSubstitute;
using Shouldly;
namespace Nethermind.BeaconNode.Test
{
    [TestClass]
    public class ValidatorAssignmentsTest
    {
        [DataTestMethod]
        [DataRow(0uL, true)]
        [DataRow(79uL, true)]
        [DataRow(80uL, false)]
        public void ValidatorShouldBeActiveAfterTestGenesis(ulong index, bool shouldBeActive)
        {
            // NOTE: Test genesis has SlotsPerEpoch (8) * 10 = 80 validators.
            
            // Arrange
            IServiceCollection testServiceCollection = TestSystem.BuildTestServiceCollection(useStore: true);
            testServiceCollection.AddSingleton<IHostEnvironment>(Substitute.For<IHostEnvironment>());
            ServiceProvider testServiceProvider = testServiceCollection.BuildServiceProvider();
            BeaconState state = TestState.PrepareTestState(testServiceProvider);
            ForkChoice forkChoice = testServiceProvider.GetService<ForkChoice>();
            // Get genesis store initialise MemoryStoreProvider with the state
            _ = forkChoice.GetGenesisStore(state);            

            // Act
            ValidatorAssignments validatorAssignments = testServiceProvider.GetService<ValidatorAssignments>();
            ValidatorIndex validatorIndex = new ValidatorIndex(index);
            bool validatorActive = validatorAssignments.CheckIfValidatorActive(state, validatorIndex);

            // Assert
            validatorActive.ShouldBe(shouldBeActive);
        }

        [DataTestMethod]
        // TODO: Values not validated against manual check or another client; just set based on first run.
        [DataRow(0uL, 6uL, 1uL)]
        public void BasicGetCommitteeAssignment(ulong index, ulong slot, ulong committeeIndex)
        {
            // Arrange
            IServiceCollection testServiceCollection = TestSystem.BuildTestServiceCollection(useStore: true);
            testServiceCollection.AddSingleton<IHostEnvironment>(Substitute.For<IHostEnvironment>());
            ServiceProvider testServiceProvider = testServiceCollection.BuildServiceProvider();
            BeaconState state = TestState.PrepareTestState(testServiceProvider);
            
            // Act
            ValidatorAssignments validatorAssignments = testServiceProvider.GetService<ValidatorAssignments>();
            ValidatorIndex validatorIndex = new ValidatorIndex(index);
            CommitteeAssignment committeeAssignment = validatorAssignments.GetCommitteeAssignment(state, Epoch.Zero, validatorIndex);

            // Assert
            Console.WriteLine("Validator [{0}] {1} in slot {2} committee {3}", 
                validatorIndex, state.Validators[(int)validatorIndex].PublicKey, committeeAssignment.Slot, committeeAssignment.CommitteeIndex);
            
            Slot expectedSlot = new Slot(slot);
            CommitteeIndex expectedCommitteeIndex = new CommitteeIndex(committeeIndex);
            committeeAssignment.Slot.ShouldBe(expectedSlot);
            committeeAssignment.CommitteeIndex.ShouldBe(expectedCommitteeIndex);
        }

        [DataTestMethod]
        // TODO: Values not validated against manual check or another client; just set based on first run.
        // invalid tests
        [DataRow("0x97f1d3a73197d7942695638c4fa9ac0fc3688c4f9774b905a14e3a3f171bac586c55e83ff97a1aeffb3af00adb22c6bb", 2uL, false, 0uL, 0uL, null)]
        [DataRow("0x123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef0", 0uL, false, 0uL, 0uL, null)]
        // epoch 0 tests
        [DataRow("0x97f1d3a73197d7942695638c4fa9ac0fc3688c4f9774b905a14e3a3f171bac586c55e83ff97a1aeffb3af00adb22c6bb", 0uL, true, 6uL, 1uL, null)]
        [DataRow("0xa572cbea904d67468808c8eb50a9450c9721db309128012543902d0ac358a62ae28f75bb8f1c7c42c39a8c5529bf0f4e", 0uL, true, 0uL, 0uL, null)]
        [DataRow("0x89ece308f9d1f0131765212deca99697b112d61f9be9a5f1f3780a51335b3ff981747a0b2ca2179b96d2c0c9024e5224", 0uL, true, 2uL, 1uL, null)]
        [DataRow("0x8774d1d544c4cc583fb649d0bbba86c2d2b5abb4c0395d7d1dac08ab1a2cc795030bdbdce6e3213154d4f2c748ccdaef", 0uL, true, 6uL, 0uL, 0uL)]
        // looking forward, find the 2uL proposal slot
        [DataRow("0x9717182463fbe215168e6762abcbb55c5c65290f2b5a2af616f8a6f50d625b46164178a11622d21913efdfa4b800648d", 0uL, true, 4uL, 0uL, 2uL)]
        // epoch 1 tests
        [DataRow("0x97f1d3a73197d7942695638c4fa9ac0fc3688c4f9774b905a14e3a3f171bac586c55e83ff97a1aeffb3af00adb22c6bb", 1uL, true, 10uL, 0uL, null)]
        [DataRow("0xa572cbea904d67468808c8eb50a9450c9721db309128012543902d0ac358a62ae28f75bb8f1c7c42c39a8c5529bf0f4e", 1uL, true, 14uL, 1uL, null)]
        [DataRow("0x89ece308f9d1f0131765212deca99697b112d61f9be9a5f1f3780a51335b3ff981747a0b2ca2179b96d2c0c9024e5224", 1uL, true, 8uL, 1uL, null)]
        [DataRow("0x95906ec0660892c205634e21ad540cbe0b6f7729d101d5c4639b864dea09be7f42a4252c675d46dd90a2661b3a94e8ca", 1uL, true, 8uL, 0uL, 9uL)]
        public async Task BasicValidatorDuty(string publicKey, ulong epoch, bool success, ulong attestationSlot, ulong attestationShard, ulong? blockProposalSlot)
        {
            // Arrange
            IServiceCollection testServiceCollection = TestSystem.BuildTestServiceCollection(useStore: true);
            testServiceCollection.AddSingleton<IHostEnvironment>(Substitute.For<IHostEnvironment>());
            ServiceProvider testServiceProvider = testServiceCollection.BuildServiceProvider();
            BeaconState state = TestState.PrepareTestState(testServiceProvider);
            ForkChoice forkChoice = testServiceProvider.GetService<ForkChoice>();
            // Get genesis store initialise MemoryStoreProvider with the state
            _ = forkChoice.GetGenesisStore(state);            
            
            // Act
            ValidatorAssignments validatorAssignments = testServiceProvider.GetService<ValidatorAssignments>();
            BlsPublicKey validatorPublicKey = new BlsPublicKey(publicKey);
            Epoch targetEpoch = new Epoch(epoch);
            
            // failure expected
            if (!success)
            {
                Should.Throw<Exception>( async () =>
                {
                    ValidatorDuty validatorDuty = await validatorAssignments.GetValidatorDutyAsync(validatorPublicKey, targetEpoch);
                    Console.WriteLine("Validator {0}, epoch {1}: attestation slot {2}, shard {3}, proposal slot {4}", 
                        validatorPublicKey, targetEpoch, validatorDuty.AttestationSlot, (ulong)validatorDuty.AttestationShard, validatorDuty.BlockProposalSlot);
                });
                return;
            }
            
            ValidatorDuty validatorDuty = await validatorAssignments.GetValidatorDutyAsync(validatorPublicKey, targetEpoch);
            Console.WriteLine("Validator {0}, epoch {1}: attestation slot {2}, shard {3}, proposal slot {4}", 
                validatorPublicKey, targetEpoch, validatorDuty.AttestationSlot, (ulong)validatorDuty.AttestationShard, validatorDuty.BlockProposalSlot);

            // Assert
            validatorDuty.ValidatorPublicKey.ShouldBe(validatorPublicKey);

            Slot expectedBlockProposalSlot = blockProposalSlot.HasValue ? new Slot(blockProposalSlot.Value) : Slot.None;
            Slot expectedAttestationSlot = new Slot(attestationSlot);
            Shard expectedAttestationShard = new Shard(attestationShard);
            
            validatorDuty.BlockProposalSlot.ShouldBe(expectedBlockProposalSlot);
            validatorDuty.AttestationSlot.ShouldBe(expectedAttestationSlot);
            validatorDuty.AttestationShard.ShouldBe(expectedAttestationShard);
        }

        [DataTestMethod]
        // time = slot 2 * 6 + 5 = 17
        [DataRow(17uL, "0x9717182463fbe215168e6762abcbb55c5c65290f2b5a2af616f8a6f50d625b46164178a11622d21913efdfa4b800648d", 0uL, 4uL, 0uL, 2uL)]
        // time = slot 3 * 6 = 18
        [DataRow(18uL, "0x9717182463fbe215168e6762abcbb55c5c65290f2b5a2af616f8a6f50d625b46164178a11622d21913efdfa4b800648d", 0uL, 4uL, 0uL, 6uL)]
        public async Task ValidatorDutyAtSpecificTime(ulong targetTime, string publicKey, ulong epoch, ulong attestationSlot, ulong attestationShard, ulong? blockProposalSlot)
        {
            // NOTE: Current algorithm for GetBeaconProposerIndex() someimtes allocates multiple proposal slots in an epoch, e.g. above index 23 gets slot 2 and 6
            // It could be an error in the algorithm (need to check; domain type could be wrong), or due to the pre-shard algorithm.
            // The algorithm before shards were removed was different, so maybe ignore for now, implement phase 1 (with shards), and worry about it then.
            // This test for now will simply detect if things unintentionally change.
            
            // Arrange
            IServiceCollection testServiceCollection = TestSystem.BuildTestServiceCollection(useStore: true);
            testServiceCollection.AddSingleton<IHostEnvironment>(Substitute.For<IHostEnvironment>());
            ServiceProvider testServiceProvider = testServiceCollection.BuildServiceProvider();
            BeaconState state = TestState.PrepareTestState(testServiceProvider);
            ForkChoice forkChoice = testServiceProvider.GetService<ForkChoice>();
            // Get genesis store initialise MemoryStoreProvider with the state
            IStore store = forkChoice.GetGenesisStore(state);            
            
            // Move forward time
            TimeParameters timeParameters = testServiceProvider.GetService<IOptions<TimeParameters>>().Value;
            for (ulong timeSinceGenesis = 1; timeSinceGenesis <= targetTime; timeSinceGenesis++)
            {
                ulong time = state.GenesisTime + timeSinceGenesis;
                await forkChoice.OnTickAsync(store, time);
                if (timeSinceGenesis % timeParameters.SecondsPerSlot == 0)
                {
                    BeaconBlock block = TestBlock.BuildEmptyBlockForNextSlot(testServiceProvider, state, signed: true);
                    TestState.StateTransitionAndSignBlock(testServiceProvider, state, block);
                    await forkChoice.OnBlockAsync(store, block);
                }
            }
            
            Console.WriteLine("");
            Console.WriteLine("***** State advanced to slot {0}, time {1}, ready to start tests *****", state.Slot, store.Time);
            Console.WriteLine("");
            
            // Act
            ValidatorAssignments validatorAssignments = testServiceProvider.GetService<ValidatorAssignments>();
            BlsPublicKey validatorPublicKey = new BlsPublicKey(publicKey);
            Epoch targetEpoch = new Epoch(epoch);

            ValidatorDuty validatorDuty = await validatorAssignments.GetValidatorDutyAsync(validatorPublicKey, targetEpoch);
            Console.WriteLine("Validator {0}, epoch {1}: attestation slot {2}, shard {3}, proposal slot {4}", 
                validatorPublicKey, targetEpoch, validatorDuty.AttestationSlot, (ulong)validatorDuty.AttestationShard, validatorDuty.BlockProposalSlot);

            // Assert
            validatorDuty.ValidatorPublicKey.ShouldBe(validatorPublicKey);

            Slot expectedBlockProposalSlot = blockProposalSlot.HasValue ? new Slot(blockProposalSlot.Value) : Slot.None;
            Slot expectedAttestationSlot = new Slot(attestationSlot);
            Shard expectedAttestationShard = new Shard(attestationShard);
            
            validatorDuty.BlockProposalSlot.ShouldBe(expectedBlockProposalSlot);
            validatorDuty.AttestationSlot.ShouldBe(expectedAttestationSlot);
            validatorDuty.AttestationShard.ShouldBe(expectedAttestationShard);
        }

        [TestMethod]
        public async Task FutureEpochValidatorDuty()
        {
            // Arrange
            IServiceCollection testServiceCollection = TestSystem.BuildTestServiceCollection(useStore: true);
            testServiceCollection.AddSingleton<IHostEnvironment>(Substitute.For<IHostEnvironment>());
            ServiceProvider testServiceProvider = testServiceCollection.BuildServiceProvider();
            BeaconState state = TestState.PrepareTestState(testServiceProvider);
            ForkChoice forkChoice = testServiceProvider.GetService<ForkChoice>();
            // Get genesis store initialise MemoryStoreProvider with the state
            IStore store = forkChoice.GetGenesisStore(state);
            
            // Move forward time
            TimeParameters timeParameters = testServiceProvider.GetService<IOptions<TimeParameters>>().Value;
            ulong time = state.GenesisTime + 1;
            ulong nextSlotTime = state.GenesisTime + timeParameters.SecondsPerSlot;
            // half way through epoch 4
            ulong futureEpoch = 4uL;
            ulong slots = futureEpoch * timeParameters.SlotsPerEpoch + timeParameters.SlotsPerEpoch / 2;
            for (ulong slot = 1; slot < slots; slot++)
            {
                while (time < nextSlotTime)
                {
                    await forkChoice.OnTickAsync(store, time);
                    time++;
                }
                await forkChoice.OnTickAsync(store, time);
                time++;
//                Hash32 head = await forkChoice.GetHeadAsync(store);
//                store.TryGetBlockState(head, out BeaconState headState);
                BeaconState headState = state;
                BeaconBlock block = TestBlock.BuildEmptyBlockForNextSlot(testServiceProvider, headState, signed: true);
                TestState.StateTransitionAndSignBlock(testServiceProvider, headState, block);
                await forkChoice.OnBlockAsync(store, block);
                nextSlotTime = nextSlotTime + timeParameters.SecondsPerSlot;
            }
            // halfway through slot
            ulong futureTime = nextSlotTime + timeParameters.SecondsPerSlot / 2;
            while (time < futureTime)
            {
                await forkChoice.OnTickAsync(store, time);
                time++;
            }
            
            Console.WriteLine("");
            Console.WriteLine("***** State advanced to epoch {0}, slot {1}, time {2}, ready to start tests *****", futureEpoch, state.Slot, store.Time);
            Console.WriteLine("");

            List<object?[]> data = FutureEpochValidatorDutyData().ToList();
            for (int dataIndex = 0; dataIndex < data.Count; dataIndex++)
            {
                object?[] dataRow = data[dataIndex];
                string publicKey = (string)dataRow[0]!;
                ulong epoch = (ulong)dataRow[1]!;
                bool success = (bool)dataRow[2]!;
                ulong attestationSlot = (ulong)dataRow[3]!;
                ulong attestationShard = (ulong)dataRow[4]!;
                ulong? blockProposalSlot = (ulong?)dataRow[5];
                
                Console.WriteLine("** Test {0}, public key {1}, epoch {2}", dataIndex, publicKey, epoch);
                    
                // Act
                ValidatorAssignments validatorAssignments = testServiceProvider.GetService<ValidatorAssignments>();
                BlsPublicKey validatorPublicKey = new BlsPublicKey(publicKey);
                Epoch targetEpoch = new Epoch(epoch);

                // failure expected
                if (!success)
                {
                    Should.Throw<Exception>(async () =>
                    {
                        ValidatorDuty validatorDuty =
                            await validatorAssignments.GetValidatorDutyAsync(validatorPublicKey, targetEpoch);
                        Console.WriteLine(
                            "Validator {0}, epoch {1}: attestation slot {2}, shard {3}, proposal slot {4}",
                            validatorPublicKey, targetEpoch, validatorDuty.AttestationSlot,
                            (ulong) validatorDuty.AttestationShard, validatorDuty.BlockProposalSlot);
                    }, $"Test {dataIndex}, public key {validatorPublicKey}, epoch {targetEpoch}");
                    continue;
                }

                ValidatorDuty validatorDuty =
                    await validatorAssignments.GetValidatorDutyAsync(validatorPublicKey, targetEpoch);
                Console.WriteLine("Validator {0}, epoch {1}: attestation slot {2}, shard {3}, proposal slot {4}",
                    validatorPublicKey, targetEpoch, validatorDuty.AttestationSlot,
                    (ulong) validatorDuty.AttestationShard, validatorDuty.BlockProposalSlot);

                // Assert
                validatorDuty.ValidatorPublicKey.ShouldBe(validatorPublicKey, $"Test {dataIndex}, public key {validatorPublicKey}, epoch {targetEpoch}");

                Slot expectedBlockProposalSlot =
                    blockProposalSlot.HasValue ? new Slot(blockProposalSlot.Value) : Slot.None;
                Slot expectedAttestationSlot = new Slot(attestationSlot);
                Shard expectedAttestationShard = new Shard(attestationShard);

                validatorDuty.BlockProposalSlot.ShouldBe(expectedBlockProposalSlot, $"Test {dataIndex}, public key {validatorPublicKey}, epoch {targetEpoch}");
                validatorDuty.AttestationSlot.ShouldBe(expectedAttestationSlot, $"Test {dataIndex}, public key {validatorPublicKey}, epoch {targetEpoch}");
                validatorDuty.AttestationShard.ShouldBe(expectedAttestationShard, $"Test {dataIndex}, public key {validatorPublicKey}, epoch {targetEpoch}");
            }
        }

        private IEnumerable<object?[]> FutureEpochValidatorDutyData()
        {
            // TODO: Values not validated against manual check or another client; just set based on first run.
            // invalid tests
            yield return new object?[]
            {
                "0x97f1d3a73197d7942695638c4fa9ac0fc3688c4f9774b905a14e3a3f171bac586c55e83ff97a1aeffb3af00adb22c6bb",
                6uL, false, 0uL, 0uL, null
            };
            yield return new object?[]
            {
                "0x123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef0",
                4uL, false, 0uL, 0uL, null
            };
            // epoch 0 tests
            yield return new object?[]
            {
                "0x8774d1d544c4cc583fb649d0bbba86c2d2b5abb4c0395d7d1dac08ab1a2cc795030bdbdce6e3213154d4f2c748ccdaef",
                0uL, true, 6uL, 0uL, 0uL
            };
            // looking backwards, should find 6uL proposal slot
            yield return new object?[]
            {
                "0x9717182463fbe215168e6762abcbb55c5c65290f2b5a2af616f8a6f50d625b46164178a11622d21913efdfa4b800648d",
                0uL, true, 4uL, 0uL, 6uL
            };
            // epoch 1 tests
            yield return new object?[]
            {
                "0x95906ec0660892c205634e21ad540cbe0b6f7729d101d5c4639b864dea09be7f42a4252c675d46dd90a2661b3a94e8ca", 1uL, true, 8uL, 0uL, 9uL
            };
            // epoch 10 tests
            yield return new object?[]
            {
                "0x97f1d3a73197d7942695638c4fa9ac0fc3688c4f9774b905a14e3a3f171bac586c55e83ff97a1aeffb3af00adb22c6bb",
                4uL, true, 36uL, 1uL, null
            };
            yield return new object?[]
            {
                "0xa572cbea904d67468808c8eb50a9450c9721db309128012543902d0ac358a62ae28f75bb8f1c7c42c39a8c5529bf0f4e",
                4uL, true, 34uL, 1uL, null
            };
            yield return new object?[]
            {
                "0x89ece308f9d1f0131765212deca99697b112d61f9be9a5f1f3780a51335b3ff981747a0b2ca2179b96d2c0c9024e5224",
                4uL, true, 35uL, 1uL, null
            };
            yield return new object?[]
            {
                "0xac9b60d5afcbd5663a8a44b7c5a02f19e9a77ab0a35bd65809bb5c67ec582c897feb04decc694b13e08587f3ff9b5b60",
                4uL, true, 39uL, 1uL, null
            };
            yield return new object?[]
            {
                "0xb0e7791fb972fe014159aa33a98622da3cdc98ff707965e536d8636b5fcc5ac7a91a8c46e59a00dca575af0f18fb13dc",
                4uL, true, 34uL, 0uL, null
            };
            // epoch 11 tests
            yield return new object?[]
            {
                "0x97f1d3a73197d7942695638c4fa9ac0fc3688c4f9774b905a14e3a3f171bac586c55e83ff97a1aeffb3af00adb22c6bb",
                5uL, true, 40uL, 1uL, null
            };
        }
    }
}