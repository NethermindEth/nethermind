using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nethermind.BeaconNode.Configuration;
using Nethermind.BeaconNode.Containers;
using Nethermind.BeaconNode.Containers.Json;
using Nethermind.BeaconNode.Ssz;
using Nethermind.BeaconNode.Storage;
using Nethermind.BeaconNode.Tests.Helpers;
using Shouldly;

namespace Nethermind.BeaconNode.Tests.Fork
{
    [TestClass]
    public class GetHeadTest
    {
        [TestMethod]
        public async Task GenesisHead()
        {
            // Arrange
            var testServiceProvider = TestSystem.BuildTestServiceProvider(useStore: true);
            var state = TestState.PrepareTestState(testServiceProvider);

            var miscellaneousParameters = testServiceProvider.GetService<IOptions<MiscellaneousParameters>>().Value;
            var timeParameters = testServiceProvider.GetService<IOptions<TimeParameters>>().Value;
            var stateListLengths = testServiceProvider.GetService<IOptions<StateListLengths>>().Value;
            var maxOperationsPerBlock = testServiceProvider.GetService<IOptions<MaxOperationsPerBlock>>().Value;

            var options = new System.Text.Json.JsonSerializerOptions { WriteIndented = true };
            options.AddCortexContainerConverters();
            var debugState = System.Text.Json.JsonSerializer.Serialize(state, options);
            
            // Initialization
            var forkChoice = testServiceProvider.GetService<ForkChoice>();
            var store = forkChoice.GetGenesisStore(state);

            // Act
            var headRoot = await forkChoice.GetHeadAsync(store);

            // Assert
            var stateRoot = state.HashTreeRoot(miscellaneousParameters, timeParameters, stateListLengths, maxOperationsPerBlock);
            var genesisBlock = new BeaconBlock(stateRoot);
            var expectedRoot = genesisBlock.SigningRoot(miscellaneousParameters, maxOperationsPerBlock);

            headRoot.ShouldBe(expectedRoot);
        }

        [TestMethod]
        public async Task ChainNoAttestations()
        {
            // Arrange
            var testServiceProvider = TestSystem.BuildTestServiceProvider(useStore: true);
            var state = TestState.PrepareTestState(testServiceProvider);

            var miscellaneousParameters = testServiceProvider.GetService<IOptions<MiscellaneousParameters>>().Value;
            var maxOperationsPerBlock = testServiceProvider.GetService<IOptions<MaxOperationsPerBlock>>().Value;

            // Initialization
            var forkChoice = testServiceProvider.GetService<ForkChoice>();
            var store = forkChoice.GetGenesisStore(state);

            // On receiving a block of `GENESIS_SLOT + 1` slot
            var block1 = TestBlock.BuildEmptyBlockForNextSlot(testServiceProvider, state, signed: true);
            TestState.StateTransitionAndSignBlock(testServiceProvider, state, block1);
            AddBlockToStore(testServiceProvider, store, block1);

            // On receiving a block of next epoch
            var block2 = TestBlock.BuildEmptyBlockForNextSlot(testServiceProvider, state, signed: true);
            TestState.StateTransitionAndSignBlock(testServiceProvider, state, block2);
            AddBlockToStore(testServiceProvider, store, block2);

            // Act
            var headRoot = await forkChoice.GetHeadAsync(store);

            // Assert
            var expectedRoot = block2.SigningRoot(miscellaneousParameters, maxOperationsPerBlock);
            headRoot.ShouldBe(expectedRoot);
        }

        [TestMethod]
        public async Task SplitTieBreakerNoAttestations()
        {
            // Arrange
            var testServiceProvider = TestSystem.BuildTestServiceProvider(useStore: true);
            var state = TestState.PrepareTestState(testServiceProvider);

            var miscellaneousParameters = testServiceProvider.GetService<IOptions<MiscellaneousParameters>>().Value;
            var maxOperationsPerBlock = testServiceProvider.GetService<IOptions<MaxOperationsPerBlock>>().Value;

            // Initialization
            var forkChoice = testServiceProvider.GetService<ForkChoice>();
            var store = forkChoice.GetGenesisStore(state);
            var genesisState = BeaconState.Clone(state);

            // block at slot 1
            var block1State = BeaconState.Clone(genesisState);
            var block1 = TestBlock.BuildEmptyBlockForNextSlot(testServiceProvider, block1State, signed: true);
            TestState.StateTransitionAndSignBlock(testServiceProvider, block1State, block1);
            AddBlockToStore(testServiceProvider, store, block1);
            var block1Root = block1.SigningRoot(miscellaneousParameters, maxOperationsPerBlock);

            // build short tree
            var block2State = BeaconState.Clone(genesisState);
            var block2 = TestBlock.BuildEmptyBlockForNextSlot(testServiceProvider, block2State, signed: true);
            block2.Body.SetGraffiti(new Bytes32(Enumerable.Repeat((byte)0x42, 32).ToArray()));
            TestBlock.SignBlock(testServiceProvider, block2State, block2, ValidatorIndex.None);
            TestState.StateTransitionAndSignBlock(testServiceProvider, block2State, block2);
            AddBlockToStore(testServiceProvider, store, block2);
            var block2Root = block2.SigningRoot(miscellaneousParameters, maxOperationsPerBlock);

            // Act
            var headRoot = await forkChoice.GetHeadAsync(store);

            // Assert
            Console.WriteLine("block1 {0}", block1Root);
            Console.WriteLine("block2 {0}", block2Root);
            var highestRoot = block1Root.CompareTo(block2Root) > 0 ? block1Root : block2Root;
            Console.WriteLine("highest {0}", highestRoot);
            headRoot.ShouldBe(highestRoot);
        }

        [TestMethod]
        public async Task ShorterChainButHeavierWeight()
        {
            // Arrange
            var testServiceProvider = TestSystem.BuildTestServiceProvider(useStore: true);
            var state = TestState.PrepareTestState(testServiceProvider);

            var miscellaneousParameters = testServiceProvider.GetService<IOptions<MiscellaneousParameters>>().Value;
            var maxOperationsPerBlock = testServiceProvider.GetService<IOptions<MaxOperationsPerBlock>>().Value;

            // Initialization
            var forkChoice = testServiceProvider.GetService<ForkChoice>();
            var store = forkChoice.GetGenesisStore(state);
            var genesisState = BeaconState.Clone(state);

            // build longer tree
            Hash32 longRoot = default;
            var longState = BeaconState.Clone(genesisState);
            for (var i = 0; i < 3; i++)
            {
                var longBlock = TestBlock.BuildEmptyBlockForNextSlot(testServiceProvider, longState, signed: true);
                TestState.StateTransitionAndSignBlock(testServiceProvider, longState, longBlock);
                AddBlockToStore(testServiceProvider, store, longBlock);
                if (i == 2)
                {
                    longRoot = longBlock.SigningRoot(miscellaneousParameters, maxOperationsPerBlock);
                }
            }

            // build short tree
            var shortState = BeaconState.Clone(genesisState);
            var shortBlock = TestBlock.BuildEmptyBlockForNextSlot(testServiceProvider, shortState, signed: true);
            shortBlock.Body.SetGraffiti(new Bytes32(Enumerable.Repeat((byte)0x42, 32).ToArray()));
            TestBlock.SignBlock(testServiceProvider, shortState, shortBlock, ValidatorIndex.None);
            TestState.StateTransitionAndSignBlock(testServiceProvider, shortState, shortBlock);
            AddBlockToStore(testServiceProvider, store, shortBlock);

            var shortAttestation = TestAttestation.GetValidAttestation(testServiceProvider, shortState, shortBlock.Slot, CommitteeIndex.None, signed: true);
            AddAttestationToStore(testServiceProvider, store, shortAttestation);

            // Act
            var headRoot = await forkChoice.GetHeadAsync(store);

            // Assert
            var expectedRoot = shortBlock.SigningRoot(miscellaneousParameters, maxOperationsPerBlock);
            headRoot.ShouldBe(expectedRoot);
            headRoot.ShouldNotBe(longRoot);
        }

        private void AddAttestationToStore(IServiceProvider testServiceProvider, IStore store, Attestation attestation)
        {
            var timeParameters = testServiceProvider.GetService<IOptions<TimeParameters>>().Value;
            var miscellaneousParameters = testServiceProvider.GetService<IOptions<MiscellaneousParameters>>().Value;
            var maxOperationsPerBlock = testServiceProvider.GetService<IOptions<MaxOperationsPerBlock>>().Value;
            var forkChoice = testServiceProvider.GetService<ForkChoice>();

            store.TryGetBlock(attestation.Data.BeaconBlockRoot, out var parentBlock);
            var parentSigningRoot = parentBlock.SigningRoot(miscellaneousParameters, maxOperationsPerBlock);
            store.TryGetBlockState(parentSigningRoot, out var preState);
            var blockTime = preState.GenesisTime + (ulong)parentBlock.Slot * timeParameters.SecondsPerSlot;
            var nextEpochTime = blockTime + (ulong)timeParameters.SlotsPerEpoch * timeParameters.SecondsPerSlot;

            if (store.Time < blockTime)
            {
                forkChoice.OnTick(store, blockTime);
            }

            forkChoice.OnAttestation(store, attestation);
        }

        private void AddBlockToStore(IServiceProvider testServiceProvider, IStore store, BeaconBlock block)
        {
            var timeParameters = testServiceProvider.GetService<IOptions<TimeParameters>>().Value;
            var forkChoice = testServiceProvider.GetService<ForkChoice>();

            store.TryGetBlockState(block.ParentRoot, out var preState);
            var blockTime = preState.GenesisTime + (ulong)block.Slot * timeParameters.SecondsPerSlot;

            if (store.Time < blockTime)
            {
                forkChoice.OnTick(store, blockTime);
            }

            forkChoice.OnBlock(store, block);
        }
    }
}
