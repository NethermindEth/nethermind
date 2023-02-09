// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nethermind.Core2.Configuration;
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
    public class BeaconStateAccessorTests
    {
        [TestMethod]
        public async Task ProposerIndexForFirstTwoEpochsMustBeValid()
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
            BeaconStateAccessor beaconStateAccessor = testServiceProvider.GetService<BeaconStateAccessor>();
            TimeParameters timeParameters = testServiceProvider.GetService<IOptions<TimeParameters>>().Value;
            ulong time = state.GenesisTime + 1;
            ulong nextSlotTime = state.GenesisTime;
            ValidatorIndex maximumValidatorIndex = new ValidatorIndex((ulong)state.Validators.Count - 1);

            ValidatorIndex validatorIndex0 = beaconStateAccessor.GetBeaconProposerIndex(state);
            Console.WriteLine("Slot {0}, time {1} = proposer index {2}", state.Slot, time, validatorIndex0);
            validatorIndex0.ShouldBeLessThanOrEqualTo(maximumValidatorIndex);

            List<ValidatorIndex> proposerIndexes = new List<ValidatorIndex>();
            proposerIndexes.Add(validatorIndex0);

            for (int slotIndex = 1; slotIndex <= 16; slotIndex++)
            {
                // Slot 1
                nextSlotTime = nextSlotTime + timeParameters.SecondsPerSlot;
                while (time < nextSlotTime)
                {
                    await forkChoice.OnTickAsync(store, time);
                    time++;
                }

                await forkChoice.OnTickAsync(store, time);
                time++;
                BeaconBlock block = TestBlock.BuildEmptyBlockForNextSlot(testServiceProvider, state, BlsSignature.Zero);
                SignedBeaconBlock signedBlock = TestState.StateTransitionAndSignBlock(testServiceProvider, state, block);
                await forkChoice.OnBlockAsync(store, signedBlock);
                await forkChoice.OnTickAsync(store, time);
                time++;

                ValidatorIndex validatorIndex = beaconStateAccessor.GetBeaconProposerIndex(state);
                Console.WriteLine("Slot {0}, time {1} = proposer index {2}", state.Slot, time, validatorIndex);
                validatorIndex.ShouldBeLessThanOrEqualTo(maximumValidatorIndex);
                proposerIndexes.Add(validatorIndex);
            }

            for (int slotIndex = 0; slotIndex < proposerIndexes.Count; slotIndex++)
            {
                ValidatorIndex proposerIndex = proposerIndexes[slotIndex];
                Validator proposer = state.Validators[(int)(ulong)proposerIndex];
                Console.WriteLine("Slot {0} = proposer index {1}, public key {2}", slotIndex, proposerIndex, proposer.PublicKey);
            }
        }
    }
}
