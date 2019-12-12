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
using Nethermind.BeaconNode.Configuration;
using Nethermind.BeaconNode.Containers;
using Nethermind.BeaconNode.Tests.Helpers;
using Nethermind.Core2.Crypto;
using Nethermind.Core2.Types;
using NSubstitute;
using Shouldly;

namespace Nethermind.BeaconNode.Tests
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
            
            var expectedSlot = new Slot(slot);
            var expectedCommitteeIndex = new CommitteeIndex(committeeIndex);
            committeeAssignment.Slot.ShouldBe(expectedSlot);
            committeeAssignment.CommitteeIndex.ShouldBe(expectedCommitteeIndex);
        }

        [DataTestMethod]
        // TODO: Values not validated against manual check or another client; just set based on first run.
        [DataRow("0x97f1d3a73197d7942695638c4fa9ac0fc3688c4f9774b905a14e3a3f171bac586c55e83ff97a1aeffb3af00adb22c6bb", 0uL, true, 6uL, 1uL, null)]
        public async Task BasicValidatorDuty(string publicKey, ulong epoch,  bool success, ulong attestationSlot, ulong attestationShard, ulong? blockProposalSlot)
        {
            // Arrange
            IServiceCollection testServiceCollection = TestSystem.BuildTestServiceCollection(useStore: true);
            testServiceCollection.AddSingleton<IHostEnvironment>(Substitute.For<IHostEnvironment>());
            ServiceProvider testServiceProvider = testServiceCollection.BuildServiceProvider();
            BeaconState state = TestState.PrepareTestState(testServiceProvider);
            ForkChoice forkChoice = testServiceProvider.GetService<ForkChoice>();
            // Get genesis store initialise MemoryStoreProvider with the state
            _ = forkChoice.GetGenesisStore(state);            
            
            TimeParameters timeParameters = testServiceProvider.GetService<IOptions<TimeParameters>>().Value;
            byte[][] privateKeys = TestKeys.PrivateKeys(timeParameters).ToArray();
            BlsPublicKey[] publicKeys = TestKeys.PublicKeys(timeParameters).ToArray();
            for (int index = 0; index < 10; index++)
            {
                Console.WriteLine("[{0}] priv:{1} pub:{2}", index, "0x" + BitConverter.ToString(privateKeys[index]).Replace("-", ""), publicKeys[index]);
            }

            // Act
            ValidatorAssignments validatorAssignments = testServiceProvider.GetService<ValidatorAssignments>();
            BlsPublicKey validatorPublicKey = new BlsPublicKey(publicKey);
            Epoch targetEpoch = new Epoch(epoch);
            
            // failure expected
            if (!success)
            {
                Should.Throw<Exception>( async () =>
                {
                    ValidatorDuty validatorDuty = await validatorAssignments.GetValidatorDutyAsync(validatorPublicKey, Epoch.Zero);
                    Console.WriteLine("Validator {0}, epoch {1}: attestation slot {2}, shard {3}, proposal slot {4}", 
                        validatorPublicKey, targetEpoch, validatorDuty.AttestationSlot, validatorDuty.AttestationShard, validatorDuty.BlockProposalSlot);
                });
                return;
            }
            
            ValidatorDuty validatorDuty = await validatorAssignments.GetValidatorDutyAsync(validatorPublicKey, Epoch.Zero);
            Console.WriteLine("Validator {0}, epoch {1}: attestation slot {2}, shard {3}, proposal slot {4}", 
                validatorPublicKey, targetEpoch, validatorDuty.AttestationSlot, validatorDuty.AttestationShard, validatorDuty.BlockProposalSlot);

            // Assert
            validatorDuty.ValidatorPublicKey.ShouldBe(validatorPublicKey);

            Slot expectedBlockProposalSlot = blockProposalSlot.HasValue ? new Slot(blockProposalSlot.Value) : Slot.None;
            Slot expectedAttestationSlot = new Slot(attestationSlot);
            Shard expectedAttestationShard = new Shard(attestationShard);
            
            validatorDuty.BlockProposalSlot.ShouldBe(expectedBlockProposalSlot);
            validatorDuty.AttestationSlot.ShouldBe(expectedAttestationSlot);
            validatorDuty.AttestationShard.ShouldBe(expectedAttestationShard);
        }
    }
}