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
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nethermind.Core2.Configuration;
using Nethermind.BeaconNode.Ssz;
using Nethermind.BeaconNode.Storage;
using Nethermind.BeaconNode.Test.Helpers;
using Nethermind.Core2;
using Nethermind.Core2.Containers;
using Nethermind.Core2.Crypto;
using Nethermind.Core2.Types;
using Shouldly;
namespace Nethermind.BeaconNode.Test.Fork
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
            ForkChoice forkChoice = testServiceProvider.GetService<ForkChoice>();

            // Initialization
            IStore store = forkChoice.GetGenesisStore(state);
            ulong time = 100uL;
            await forkChoice.OnTickAsync(store, time);
            store.Time.ShouldBe(time);

            // On receiving a block of `GENESIS_SLOT + 1` slot
            BeaconBlock block = TestBlock.BuildEmptyBlockForNextSlot(testServiceProvider, state, signed: true);
            TestState.StateTransitionAndSignBlock(testServiceProvider, state, block);
            await RunOnBlock(testServiceProvider, store, block, expectValid: true);

            //  On receiving a block of next epoch
            ulong time2 = time + timeParameters.SecondsPerSlot * (ulong)timeParameters.SlotsPerEpoch;
            await store.SetTimeAsync(time2);
            BeaconBlock block2 = TestBlock.BuildEmptyBlockForNextSlot(testServiceProvider, state, signed: true);
            Slot slot2 = (Slot)(block.Slot + timeParameters.SlotsPerEpoch);
            block2.SetSlot(slot2);
            TestBlock.SignBlock(testServiceProvider, state, block2, ValidatorIndex.None);
            TestState.StateTransitionAndSignBlock(testServiceProvider, state, block2);

            //var options = new System.Text.Json.JsonSerializerOptions { WriteIndented = true };
            //options.AddCortexContainerConverters();
            //var debugState = System.Text.Json.JsonSerializer.Serialize(state, options);

            await RunOnBlock(testServiceProvider, store, block2, expectValid: true);

            // Assert
            // TODO: add tests for justified_root and finalized_root
        }

        private async Task RunOnBlock(IServiceProvider testServiceProvider, IStore store, BeaconBlock block, bool expectValid)
        {
            MiscellaneousParameters miscellaneousParameters = testServiceProvider.GetService<IOptions<MiscellaneousParameters>>().Value;
            MaxOperationsPerBlock maxOperationsPerBlock = testServiceProvider.GetService<IOptions<MaxOperationsPerBlock>>().Value;

            ForkChoice forkChoice = testServiceProvider.GetService<ForkChoice>();

            if (!expectValid)
            {
                Should.Throw<Exception>(async () =>
                {
                    await forkChoice.OnBlockAsync(store, block);
                });
                return;
            }

            await forkChoice.OnBlockAsync(store, block);
            Hash32 signingRoot = block.SigningRoot(miscellaneousParameters, maxOperationsPerBlock);
            BeaconBlock storedBlock = await store.GetBlockAsync(signingRoot);
            storedBlock.ShouldBe(block);
        }
    }
}
