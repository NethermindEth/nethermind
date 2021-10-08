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
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nethermind.BeaconNode.Test.Helpers;
using Nethermind.Core2;
using Nethermind.Core2.Api;
using Nethermind.Core2.Configuration;
using Nethermind.Core2.Containers;
using Nethermind.Core2.Crypto;
using Nethermind.Core2.Types;
using NSubstitute;
using Shouldly;

namespace Nethermind.BeaconNode.Test
{
    [TestClass]
    public class ValidatorAssignmentsTests
    {
        [DataTestMethod]
        // TODO: Values not validated against manual check or another client; just set based on first run.
        [DataRow(0uL, 3uL, 0uL)]
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
            CommitteeAssignment committeeAssignment =
                validatorAssignments.GetCommitteeAssignment(state, Epoch.Zero, validatorIndex);

            // Assert
            Console.WriteLine("Validator [{0}] {1} in slot {2} committee {3}",
                validatorIndex, state.Validators[(int) validatorIndex].PublicKey, committeeAssignment.Slot,
                committeeAssignment.CommitteeIndex);

            committeeAssignment.ShouldNotBe(CommitteeAssignment.None);
            committeeAssignment.Committee.Count.ShouldBeGreaterThan(0);

            Slot expectedSlot = new Slot(slot);
            CommitteeIndex expectedCommitteeIndex = new CommitteeIndex(committeeIndex);
            committeeAssignment.Slot.ShouldBe(expectedSlot);
            committeeAssignment.CommitteeIndex.ShouldBe(expectedCommitteeIndex);
        }

        [DataTestMethod]
        // TODO: Values not validated against manual check or another client; just set based on first run.
        // invalid tests (exception as epoch too far in future)
        [DataRow("0x97f1d3a73197d7942695638c4fa9ac0fc3688c4f9774b905a14e3a3f171bac586c55e83ff97a1aeffb3af00adb22c6bb",
            2uL, false, null, null, null)]
        // invalid tests (return null duty)
        [DataRow("0x123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef0",
            0uL, true, null, null, null)]
        // epoch 0 tests
        [DataRow("0x97f1d3a73197d7942695638c4fa9ac0fc3688c4f9774b905a14e3a3f171bac586c55e83ff97a1aeffb3af00adb22c6bb",
            0uL, true, 3uL, 0uL, null)]
        [DataRow("0xa572cbea904d67468808c8eb50a9450c9721db309128012543902d0ac358a62ae28f75bb8f1c7c42c39a8c5529bf0f4e",
            0uL, true, 6uL, 1uL, null)]
        [DataRow("0x89ece308f9d1f0131765212deca99697b112d61f9be9a5f1f3780a51335b3ff981747a0b2ca2179b96d2c0c9024e5224",
            0uL, true, 6uL, 1uL, null)]
        [DataRow("0x8345dd80ffef0eaec8920e39ebb7f5e9ae9c1d6179e9129b705923df7830c67f3690cbc48649d4079eadf5397339580c",
            0uL, true, 4uL, 1uL, 0uL)]
        // looking forward, find the 2uL proposal slot
        [DataRow("0x9717182463fbe215168e6762abcbb55c5c65290f2b5a2af616f8a6f50d625b46164178a11622d21913efdfa4b800648d",
            0uL, true, 5uL, 1uL, null)]
        // epoch 1 tests
        [DataRow("0x97f1d3a73197d7942695638c4fa9ac0fc3688c4f9774b905a14e3a3f171bac586c55e83ff97a1aeffb3af00adb22c6bb",
            1uL, true, 13uL, 0uL, null)]
        [DataRow("0xa572cbea904d67468808c8eb50a9450c9721db309128012543902d0ac358a62ae28f75bb8f1c7c42c39a8c5529bf0f4e",
            1uL, true, 13uL, 1uL, null)]
        [DataRow("0x89ece308f9d1f0131765212deca99697b112d61f9be9a5f1f3780a51335b3ff981747a0b2ca2179b96d2c0c9024e5224",
            1uL, true, 9uL, 0uL, null)]
        [DataRow("0x8515e7f61ca0470e165a44d247a23f17f24bf6e37185467bedb7981c1003ea70bbec875703f793dd8d11e56afa7f74ba",
            1uL, true, 15uL, 0uL, 9uL)]
        public async Task BasicValidatorDuty(string publicKey, ulong epoch, bool success, ulong? attestationSlot,
            ulong? attestationIndex, ulong? blockProposalSlot)
        {
            // Arrange
            IServiceCollection testServiceCollection = TestSystem.BuildTestServiceCollection(useStore: true);
            testServiceCollection.AddSingleton<IHostEnvironment>(Substitute.For<IHostEnvironment>());
            ServiceProvider testServiceProvider = testServiceCollection.BuildServiceProvider();
            BeaconState state = TestState.PrepareTestState(testServiceProvider);
            IForkChoice forkChoice = testServiceProvider.GetService<IForkChoice>();
            // Get genesis store initialise MemoryStoreProvider with the state
            IStore store = testServiceProvider.GetService<IStore>();
            await forkChoice.InitializeForkChoiceStoreAsync(store, state);

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
                    Console.WriteLine("Validator {0}, epoch {1}: attestation slot {2}, attestation index {3}, proposal slot {4}",
                        validatorPublicKey, targetEpoch, validatorDuty.AttestationSlot,
                        validatorDuty.AttestationIndex, validatorDuty.BlockProposalSlot);
                });
                return;
            }

            ValidatorDuty validatorDuty =
                await validatorAssignments.GetValidatorDutyAsync(validatorPublicKey, targetEpoch);
            Console.WriteLine("Validator {0}, epoch {1}: attestation slot {2}, attestation index {3}, proposal slot {4}",
                validatorPublicKey, targetEpoch, validatorDuty.AttestationSlot, validatorDuty.AttestationIndex,
                validatorDuty.BlockProposalSlot);

            // Assert
            validatorDuty.ValidatorPublicKey.ShouldBe(validatorPublicKey);

            Slot? expectedBlockProposalSlot = (Slot?) blockProposalSlot;
            Slot? expectedAttestationSlot = (Slot?) attestationSlot;
            CommitteeIndex? expectedAttestationIndex = (CommitteeIndex?) attestationIndex;

            validatorDuty.BlockProposalSlot.ShouldBe(expectedBlockProposalSlot);
            validatorDuty.AttestationSlot.ShouldBe(expectedAttestationSlot);
            validatorDuty.AttestationIndex.ShouldBe(expectedAttestationIndex);
        }

        [TestMethod]
        public async Task FutureEpochValidatorDuty()
        {
            // Arrange
            IServiceCollection testServiceCollection = TestSystem.BuildTestServiceCollection(useStore: true);
            testServiceCollection.AddSingleton<IHostEnvironment>(Substitute.For<IHostEnvironment>());
            ServiceProvider testServiceProvider = testServiceCollection.BuildServiceProvider();
            BeaconState state = TestState.PrepareTestState(testServiceProvider);
            IForkChoice forkChoice = testServiceProvider.GetService<IForkChoice>();
            // Get genesis store initialise MemoryStoreProvider with the state
            IStore store = testServiceProvider.GetService<IStore>();
            await forkChoice.InitializeForkChoiceStoreAsync(store, state);

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
                BeaconBlock block =
                    TestBlock.BuildEmptyBlockForNextSlot(testServiceProvider, headState, BlsSignature.Zero);
                SignedBeaconBlock signedBlock =
                    TestState.StateTransitionAndSignBlock(testServiceProvider, headState, block);
                await forkChoice.OnBlockAsync(store, signedBlock);
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
            Console.WriteLine("***** State advanced to epoch {0}, slot {1}, time {2}, ready to start tests *****",
                futureEpoch, state.Slot, store.Time);
            Console.WriteLine("");

            List<object?[]> data = FutureEpochValidatorDutyData().ToList();
            for (int dataIndex = 0; dataIndex < data.Count; dataIndex++)
            {
                object?[] dataRow = data[dataIndex];
                string publicKey = (string) dataRow[0]!;
                ulong epoch = (ulong) dataRow[1]!;
                bool success = (bool) dataRow[2]!;
                ulong? attestationSlot = (ulong?) dataRow[3]!;
                ulong? attestationIndex = (ulong?) dataRow[4]!;
                ulong? blockProposalSlot = (ulong?) dataRow[5];

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
                            "Validator {0}, epoch {1}: attestation slot {2}, attestation index {3}, proposal slot {4}",
                            validatorPublicKey, targetEpoch, validatorDuty.AttestationSlot,
                            validatorDuty.AttestationIndex, validatorDuty.BlockProposalSlot);
                    }, $"Test {dataIndex}, public key {validatorPublicKey}, epoch {targetEpoch}");
                    continue;
                }

                ValidatorDuty validatorDuty =
                    await validatorAssignments.GetValidatorDutyAsync(validatorPublicKey, targetEpoch);
                Console.WriteLine("Validator {0}, epoch {1}: attestation slot {2}, attestation index {3}, proposal slot {4}",
                    validatorPublicKey, targetEpoch, validatorDuty.AttestationSlot,
                    validatorDuty.AttestationIndex, validatorDuty.BlockProposalSlot);

                // Assert
                validatorDuty.ValidatorPublicKey.ShouldBe(validatorPublicKey,
                    $"Test {dataIndex}, public key {validatorPublicKey}, epoch {targetEpoch}");

                Slot? expectedBlockProposalSlot = (Slot?) blockProposalSlot;
                Slot? expectedAttestationSlot = (Slot?) attestationSlot;
                CommitteeIndex? expectedAttestationIndex = (CommitteeIndex?) attestationIndex;

                validatorDuty.BlockProposalSlot.ShouldBe(expectedBlockProposalSlot,
                    $"Test {dataIndex}, public key {validatorPublicKey}, epoch {targetEpoch}");
                validatorDuty.AttestationSlot.ShouldBe(expectedAttestationSlot,
                    $"Test {dataIndex}, public key {validatorPublicKey}, epoch {targetEpoch}");
                validatorDuty.AttestationIndex.ShouldBe(expectedAttestationIndex,
                    $"Test {dataIndex}, public key {validatorPublicKey}, epoch {targetEpoch}");
            }
        }

        // TODO: FInd some attester values to use
        [DataTestMethod]
        // time = slot 0 * 6 + 5 = 5
        [DataRow(5uL,
            "0x8345dd80ffef0eaec8920e39ebb7f5e9ae9c1d6179e9129b705923df7830c67f3690cbc48649d4079eadf5397339580c", 0uL,
            4uL, 1uL, 0uL)]
        // time = slot 1 * 6 = 6
        [DataRow(6uL,
            "0x8345dd80ffef0eaec8920e39ebb7f5e9ae9c1d6179e9129b705923df7830c67f3690cbc48649d4079eadf5397339580c", 0uL,
            4uL, 1uL, 0uL)]
        // [DataRow(42uL, "0xab48aa2cc6f4a0bb63b5d67be54ac3aed10326dda304c5aeb9e942b40d6e7610478377680ab90e092ef1895e62786008", 0uL, 5uL, 1uL, null)]
        // [DataRow(42uL, "0x8aea7d8eb22063bcfe882e2b7efc0b3713e1a48dd8343bed523b1ab4546114be84d00f896d33c605d1f67456e8e2ed93", 0uL, 5uL, 1uL, null)]
        // [DataRow(42uL, "0x89db41a6183c2fe47cf54d1e00c3cfaae53df634a32cccd5cf0c0a73e95ee0450fc3d060bb6878780fbf5f30d9e29aac", 0uL, 5uL, 1uL, null)]
        // [DataRow(42uL, "0xb783a70a1cf9f53e7d2ddf386bea81a947e5360c5f1e0bf004fceedb2073e4dd180ef3d2d91bee7b1c5a88d1afd11c49", 0uL, 5uL, 1uL, null)]
        // [DataRow(42uL, "0x8fe55d12257709ae842f8594f9a0a40de3d38dabdf82b21a60baac927e52ed00c5fd42f4c905410eacdaf8f8a9952490", 0uL, 5uL, 1uL, null)]
        // [DataRow(42uL, "0x95906ec0660892c205634e21ad540cbe0b6f7729d101d5c4639b864dea09be7f42a4252c675d46dd90a2661b3a94e8ca", 0uL, 5uL, 1uL, null)]
        public async Task ValidatorDutyAtSpecificTimeDuplicateProposal(ulong targetTime, string publicKey, ulong epoch,
            ulong attestationSlot, ulong attestationIndex, ulong? blockProposalSlot)
        {
            // NOTE: Current algorithm for GetBeaconProposerIndex() sometimes allocates multiple proposal slots in an epoch, e.g. above index 23 gets slot 2 and 6
            // It could be an error in the algorithm (need to check; domain type could be wrong), or due to the pre-shard algorithm.
            // The algorithm before shards were removed was different, so maybe ignore for now, implement phase 1 (with shards), and worry about it then.
            // This test for now will simply detect if things unintentionally change.

            // The implementation of GetValidatorDuties will always return the first assigned slot in an epoch.
            // This means it is consistent whether run before or after the first slot.

            // Arrange
            IServiceCollection testServiceCollection = TestSystem.BuildTestServiceCollection(useStore: true);
            testServiceCollection.AddSingleton<IHostEnvironment>(Substitute.For<IHostEnvironment>());
            ServiceProvider testServiceProvider = testServiceCollection.BuildServiceProvider();
            BeaconState state = TestState.PrepareTestState(testServiceProvider);
            IForkChoice forkChoice = testServiceProvider.GetService<IForkChoice>();
            // Get genesis store initialise MemoryStoreProvider with the state
            IStore store = testServiceProvider.GetService<IStore>();
            await forkChoice.InitializeForkChoiceStoreAsync(store, state);

            // Move forward time
            TimeParameters timeParameters = testServiceProvider.GetService<IOptions<TimeParameters>>().Value;
            for (ulong timeSinceGenesis = 1; timeSinceGenesis <= targetTime; timeSinceGenesis++)
            {
                ulong time = state.GenesisTime + timeSinceGenesis;
                await forkChoice.OnTickAsync(store, time);
                if (timeSinceGenesis % timeParameters.SecondsPerSlot == 0)
                {
                    BeaconBlock block =
                        TestBlock.BuildEmptyBlockForNextSlot(testServiceProvider, state, BlsSignature.Zero);
                    SignedBeaconBlock signedBlock =
                        TestState.StateTransitionAndSignBlock(testServiceProvider, state, block);
                    await forkChoice.OnBlockAsync(store, signedBlock);
                }
            }

            Console.WriteLine("");
            Console.WriteLine("***** State advanced to slot {0}, time {1}, ready to start tests *****", state.Slot,
                store.Time);
            Console.WriteLine("");

            // Act
            ValidatorAssignments validatorAssignments = testServiceProvider.GetService<ValidatorAssignments>();
            BlsPublicKey validatorPublicKey = new BlsPublicKey(publicKey);
            Epoch targetEpoch = new Epoch(epoch);

            ValidatorDuty validatorDuty =
                await validatorAssignments.GetValidatorDutyAsync(validatorPublicKey, targetEpoch);
            Console.WriteLine("Validator {0}, epoch {1}: attestation slot {2}, attestation index {3}, proposal slot {4}",
                validatorPublicKey, targetEpoch, validatorDuty.AttestationSlot, validatorDuty.AttestationIndex,
                validatorDuty.BlockProposalSlot);

            // Assert
            validatorDuty.ValidatorPublicKey.ShouldBe(validatorPublicKey);

            Slot? expectedBlockProposalSlot = (Slot?) blockProposalSlot;
            Slot? expectedAttestationSlot = (Slot?) attestationSlot;
            CommitteeIndex? expectedAttestationIndex = (CommitteeIndex?) attestationIndex;

            validatorDuty.BlockProposalSlot.ShouldBe(expectedBlockProposalSlot);
            validatorDuty.AttestationSlot.ShouldBe(expectedAttestationSlot);
            validatorDuty.AttestationIndex.ShouldBe(expectedAttestationIndex);
        }

        [DataTestMethod]
        [DataRow(0uL, true)]
        [DataRow(79uL, true)]
        [DataRow(80uL, false)]
        public async Task ValidatorShouldBeActiveAfterTestGenesis(ulong index, bool shouldBeActive)
        {
            // NOTE: Test genesis has SlotsPerEpoch (8) * 10 = 80 validators.

            // Arrange
            IServiceCollection testServiceCollection = TestSystem.BuildTestServiceCollection(useStore: true);
            testServiceCollection.AddSingleton<IHostEnvironment>(Substitute.For<IHostEnvironment>());
            ServiceProvider testServiceProvider = testServiceCollection.BuildServiceProvider();
            BeaconState state = TestState.PrepareTestState(testServiceProvider);
            IForkChoice forkChoice = testServiceProvider.GetService<IForkChoice>();
            // Get genesis store initialise MemoryStoreProvider with the state
            IStore store = testServiceProvider.GetService<IStore>();
            await forkChoice.InitializeForkChoiceStoreAsync(store, state);

            // Act
            ValidatorAssignments validatorAssignments = testServiceProvider.GetService<ValidatorAssignments>();
            ValidatorIndex validatorIndex = new ValidatorIndex(index);
            bool validatorActive = validatorAssignments.CheckIfValidatorActive(state, validatorIndex);

            // Assert
            validatorActive.ShouldBe(shouldBeActive);
        }

        private IEnumerable<object?[]> FutureEpochValidatorDutyData()
        {
            // TODO: Values not validated against manual check or another client; just set based on first run.

            // invalid tests (throw exception because requested epoch is too far in the future)
            yield return new object?[]
            {
                "0x97f1d3a73197d7942695638c4fa9ac0fc3688c4f9774b905a14e3a3f171bac586c55e83ff97a1aeffb3af00adb22c6bb",
                6uL, false, null, null, null
            };
            // invalid tests (return a duty with null values)
            yield return new object?[]
            {
                "0x123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef0",
                4uL, true, null, null, null
            };
            // epoch 0 tests
            yield return new object?[]
            {
                "0x8345dd80ffef0eaec8920e39ebb7f5e9ae9c1d6179e9129b705923df7830c67f3690cbc48649d4079eadf5397339580c",
                0uL, true, 4uL, 1uL, 0uL
            };
            // looking backwards, should find 6uL proposal slot
            yield return new object?[]
            {
                "0x9717182463fbe215168e6762abcbb55c5c65290f2b5a2af616f8a6f50d625b46164178a11622d21913efdfa4b800648d",
                0uL, true, 5uL, 1uL, null
            };
            // epoch 1 tests
            yield return new object?[]
            {
                "0x8515e7f61ca0470e165a44d247a23f17f24bf6e37185467bedb7981c1003ea70bbec875703f793dd8d11e56afa7f74ba",
                1uL, true, 15uL, 0uL, 9uL
            };
            // epoch 10 tests
            yield return new object?[]
            {
                "0x97f1d3a73197d7942695638c4fa9ac0fc3688c4f9774b905a14e3a3f171bac586c55e83ff97a1aeffb3af00adb22c6bb",
                4uL, true, 32uL, 1uL, null
            };
            yield return new object?[]
            {
                "0xa572cbea904d67468808c8eb50a9450c9721db309128012543902d0ac358a62ae28f75bb8f1c7c42c39a8c5529bf0f4e",
                4uL, true, 32uL, 0uL, null
            };
            yield return new object?[]
            {
                "0x89ece308f9d1f0131765212deca99697b112d61f9be9a5f1f3780a51335b3ff981747a0b2ca2179b96d2c0c9024e5224",
                4uL, true, 34uL, 1uL, null
            };
            yield return new object?[]
            {
                "0xac9b60d5afcbd5663a8a44b7c5a02f19e9a77ab0a35bd65809bb5c67ec582c897feb04decc694b13e08587f3ff9b5b60",
                4uL, true, 36uL, 0uL, null
            };
            yield return new object?[]
            {
                "0xb0e7791fb972fe014159aa33a98622da3cdc98ff707965e536d8636b5fcc5ac7a91a8c46e59a00dca575af0f18fb13dc",
                4uL, true, 33uL, 0uL, null
            };
            // epoch 11 tests
            yield return new object?[]
            {
                "0x97f1d3a73197d7942695638c4fa9ac0fc3688c4f9774b905a14e3a3f171bac586c55e83ff97a1aeffb3af00adb22c6bb",
                5uL, true, 42uL, 1uL, null
            };
        }
    }
}