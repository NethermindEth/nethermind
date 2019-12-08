using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nethermind.BeaconNode.Configuration;
using Nethermind.BeaconNode.Containers;
using Nethermind.BeaconNode.Ssz;
using Nethermind.BeaconNode.Storage;
using Nethermind.BeaconNode.Tests.Helpers;
using Nethermind.Core2.Types;
using Shouldly;

namespace Nethermind.BeaconNode.Tests.Fork
{
    [TestClass]
    public class OnBlockTest
    {
        [TestMethod]
        public void BasicOnBlock()
        {
            // Arrange
            var testServiceProvider = TestSystem.BuildTestServiceProvider(useStore: true);
            var state = TestState.PrepareTestState(testServiceProvider);

            var timeParameters = testServiceProvider.GetService<IOptions<TimeParameters>>().Value;
            var forkChoice = testServiceProvider.GetService<ForkChoice>();

            // Initialization
            var store = forkChoice.GetGenesisStore(state);
            var time = 100uL;
            forkChoice.OnTick(store, time);
            store.Time.ShouldBe(time);

            // On receiving a block of `GENESIS_SLOT + 1` slot
            var block = TestBlock.BuildEmptyBlockForNextSlot(testServiceProvider, state, signed: true);
            TestState.StateTransitionAndSignBlock(testServiceProvider, state, block);
            RunOnBlock(testServiceProvider, store, block, expectValid: true);

            //  On receiving a block of next epoch
            var time2 = time + timeParameters.SecondsPerSlot * (ulong)timeParameters.SlotsPerEpoch;
            store.SetTime(time2);
            var block2 = TestBlock.BuildEmptyBlockForNextSlot(testServiceProvider, state, signed: true);
            Slot slot2 = (Slot)(block.Slot + timeParameters.SlotsPerEpoch);
            block2.SetSlot(slot2);
            TestBlock.SignBlock(testServiceProvider, state, block2, ValidatorIndex.None);
            TestState.StateTransitionAndSignBlock(testServiceProvider, state, block2);

            //var options = new System.Text.Json.JsonSerializerOptions { WriteIndented = true };
            //options.AddCortexContainerConverters();
            //var debugState = System.Text.Json.JsonSerializer.Serialize(state, options);

            RunOnBlock(testServiceProvider, store, block2, expectValid: true);

            // Assert
            // TODO: add tests for justified_root and finalized_root
        }

        private void RunOnBlock(IServiceProvider testServiceProvider, IStore store, BeaconBlock block, bool expectValid)
        {
            var miscellaneousParameters = testServiceProvider.GetService<IOptions<MiscellaneousParameters>>().Value;
            var maxOperationsPerBlock = testServiceProvider.GetService<IOptions<MaxOperationsPerBlock>>().Value;

            var forkChoice = testServiceProvider.GetService<ForkChoice>();

            if (!expectValid)
            {
                Should.Throw<Exception>(() =>
                {
                    forkChoice.OnBlock(store, block);
                });
                return;
            }

            forkChoice.OnBlock(store, block);
            var signingRoot = block.SigningRoot(miscellaneousParameters, maxOperationsPerBlock);
            var storeBlock = store.Blocks[signingRoot];
            storeBlock.ShouldBe(block);
        }
    }
}
