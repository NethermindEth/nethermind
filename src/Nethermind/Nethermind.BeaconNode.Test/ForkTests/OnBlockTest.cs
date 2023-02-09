// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
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
    public class OnBlockTest
    {
        [TestMethod]
        public async Task BasicOnBlock()
        {
            // Arrange
            IServiceProvider testServiceProvider = TestSystem.BuildTestServiceProvider(useStore: true);
            BeaconState state = TestState.PrepareTestState(testServiceProvider);

            TimeParameters timeParameters = testServiceProvider.GetService<IOptions<TimeParameters>>().Value;
            IForkChoice forkChoice = testServiceProvider.GetService<IForkChoice>();

            // Initialization
            IStore store = testServiceProvider.GetService<IStore>();
            await forkChoice.InitializeForkChoiceStoreAsync(store, state);
            ulong time = 100uL;
            await forkChoice.OnTickAsync(store, time);
            store.Time.ShouldBe(time);

            // On receiving a block of `GENESIS_SLOT + 1` slot
            BeaconBlock block = TestBlock.BuildEmptyBlockForNextSlot(testServiceProvider, state, BlsSignature.Zero);
            SignedBeaconBlock signedBlock = TestState.StateTransitionAndSignBlock(testServiceProvider, state, block);
            await RunOnBlock(testServiceProvider, store, signedBlock, expectValid: true);

            //  On receiving a block of next epoch
            ulong time2 = time + timeParameters.SecondsPerSlot * (ulong)timeParameters.SlotsPerEpoch;
            await store.SetTimeAsync(time2);
            Slot slot2 = (Slot)(block.Slot + timeParameters.SlotsPerEpoch);
            BeaconBlock block2 = TestBlock.BuildEmptyBlock(testServiceProvider, state, slot2, BlsSignature.Zero);
            SignedBeaconBlock signedBlock2 = TestState.StateTransitionAndSignBlock(testServiceProvider, state, block2);

            //var options = new System.Text.Json.JsonSerializerOptions { WriteIndented = true };
            //options.AddCortexContainerConverters();
            //var debugState = System.Text.Json.JsonSerializer.Serialize(state, options);

            await RunOnBlock(testServiceProvider, store, signedBlock2, expectValid: true);

            // Assert
            // TODO: add tests for justified_root and finalized_root
        }

        private async Task RunOnBlock(IServiceProvider testServiceProvider, IStore store, SignedBeaconBlock signedBlock, bool expectValid)
        {
            ICryptographyService cryptographyService = testServiceProvider.GetService<ICryptographyService>();
            IForkChoice forkChoice = testServiceProvider.GetService<IForkChoice>();

            if (!expectValid)
            {
                Should.Throw<Exception>(async () =>
                {
                    await forkChoice.OnBlockAsync(store, signedBlock);
                });
                return;
            }

            await forkChoice.OnBlockAsync(store, signedBlock);

            Root blockRoot = cryptographyService.HashTreeRoot(signedBlock.Message);
            BeaconBlock storedBlock = (await store.GetSignedBlockAsync(blockRoot)).Message;
            storedBlock.ShouldBe(signedBlock.Message);
        }
    }
}
