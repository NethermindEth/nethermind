using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nethermind.BeaconNode.Configuration;
using Nethermind.BeaconNode.Containers;
using Nethermind.BeaconNode.Ssz;
using Nethermind.BeaconNode.Storage;
using Nethermind.BeaconNode.Tests.Helpers;
using Nethermind.Core2.Crypto;
using Nethermind.Core2.Types;
using NSubstitute;
using Shouldly;

namespace Nethermind.BeaconNode.Tests.Storage
{
    [TestClass]
    public class MemoryStoreProviderTest
    {
        private TestContext? _testContext;

        public TestContext TestContext
        {
            get => _testContext!;
            set => _testContext = value;
        }

        [TestMethod]
        public async Task HistoricalBlocksShouldBeStored()
        {
            // Arrange
            IServiceCollection testServiceCollection = TestSystem.BuildTestServiceCollection(useStore: true);
            testServiceCollection.AddSingleton<IHostEnvironment>(Substitute.For<IHostEnvironment>());
            ServiceProvider testServiceProvider = testServiceCollection.BuildServiceProvider();
            BeaconState state = TestState.PrepareTestState(testServiceProvider);
            ForkChoice forkChoice = testServiceProvider.GetService<ForkChoice>();
            // Get genesis store initialise MemoryStoreProvider with the state
            IStore store = forkChoice.GetGenesisStore(state);

            MiscellaneousParameters miscellaneousParameters =
                testServiceProvider.GetService<IOptions<MiscellaneousParameters>>().Value;
            TimeParameters timeParameters =
                testServiceProvider.GetService<IOptions<TimeParameters>>().Value;
            StateListLengths stateListLengths =
                testServiceProvider.GetService<IOptions<StateListLengths>>().Value;
            MaxOperationsPerBlock maxOperationsPerBlock =
                testServiceProvider.GetService<IOptions<MaxOperationsPerBlock>>().Value;

            // Move forward time
            ulong targetTime = 10 * 6; // part way into epoch 2
            for (ulong timeSinceGenesis = 1; timeSinceGenesis <= targetTime; timeSinceGenesis++)
            {
                ulong time = state.GenesisTime + timeSinceGenesis;
                forkChoice.OnTick(store, time);
                if (timeSinceGenesis % timeParameters.SecondsPerSlot == 0)
                {
                    BeaconBlock block = TestBlock.BuildEmptyBlockForNextSlot(testServiceProvider, state, signed: true);
                    TestState.StateTransitionAndSignBlock(testServiceProvider, state, block);
                    forkChoice.OnBlock(store, block);
                }
            }
            
            TestContext.WriteLine("");
            TestContext.WriteLine("***** State advanced to slot {0}, time {1}, ready to start tests *****", state.Slot, store.Time);
            TestContext.WriteLine("");
            
            // Act
            IStoreProvider storeProvider = testServiceProvider.GetService<IStoreProvider>();
            storeProvider.ShouldBeOfType(typeof(MemoryStoreProvider));
            storeProvider.TryGetStore(out IStore? retrievedStore).ShouldBeTrue();
            retrievedStore!.ShouldBeOfType(typeof(MemoryStore));

            Hash32 headRoot = await forkChoice.GetHeadAsync(store);
            Hash32 block2Root = forkChoice.GetAncestor(store, headRoot, new Slot(2));
            Hash32 block1Root = forkChoice.GetAncestor(store, block2Root, Slot.One);
            Hash32 genesisRoot = forkChoice.GetAncestor(store, block1Root, Slot.Zero);

            store.TryGetBlock(headRoot, out BeaconBlock? headBlock).ShouldBeTrue();
            store.TryGetBlock(block2Root, out BeaconBlock? block2).ShouldBeTrue();
            store.TryGetBlock(block1Root, out BeaconBlock? block1).ShouldBeTrue();
            store.TryGetBlock(genesisRoot, out BeaconBlock? genesisBlock).ShouldBeTrue();

            store.TryGetBlockState(headRoot, out BeaconState? headState).ShouldBeTrue();
            store.TryGetBlockState(block2Root, out BeaconState? block2State).ShouldBeTrue();
            store.TryGetBlockState(block1Root, out BeaconState? block1State).ShouldBeTrue();
            store.TryGetBlockState(genesisRoot, out BeaconState? genesisState).ShouldBeTrue();

            TestContext.WriteLine("Genesis lookup root: {0}", genesisRoot);
            TestContext.WriteLine("Block 1 lookup root: {0}", block1Root);
            TestContext.WriteLine("Block 2 lookup root: {0}", block2Root);
            TestContext.WriteLine("");

            TestContext.WriteLine("Genesis sign root: {0}, hash root: {1}",
                genesisBlock!.SigningRoot(miscellaneousParameters, maxOperationsPerBlock),
                genesisBlock!.HashTreeRoot(miscellaneousParameters, maxOperationsPerBlock));
            TestContext.WriteLine("Block 1 sign root: {0}, hash root: {1}",
                block1!.SigningRoot(miscellaneousParameters, maxOperationsPerBlock),
                block1!.HashTreeRoot(miscellaneousParameters, maxOperationsPerBlock));
            TestContext.WriteLine("Block2 sign root: {0}, hash root: {1}",
                block2!.SigningRoot(miscellaneousParameters, maxOperationsPerBlock),
                block2!.HashTreeRoot(miscellaneousParameters, maxOperationsPerBlock));
            TestContext.WriteLine("");

            TestContext.WriteLine("Genesis state root: {0}", genesisBlock!.StateRoot);
            TestContext.WriteLine("Block state 1 root: {0}", block1!.StateRoot);
            TestContext.WriteLine("Block state 2 root: {0}", block2!.StateRoot);
            TestContext.WriteLine("");

            TestContext.WriteLine("Genesis state hash root: {0}",
                genesisState!.HashTreeRoot(miscellaneousParameters, timeParameters, stateListLengths, maxOperationsPerBlock));
            TestContext.WriteLine("State 1 hash root: {0}",
                block1State!.HashTreeRoot(miscellaneousParameters, timeParameters, stateListLengths, maxOperationsPerBlock));
            TestContext.WriteLine("State 2 hash root: {0}",
                block2State!.HashTreeRoot(miscellaneousParameters, timeParameters, stateListLengths, maxOperationsPerBlock));
            TestContext.WriteLine("");

            TestContext.WriteLine("Genesis state last block root: {0}, latest block header parent {1}, signing root {2}",
                genesisState!.BlockRoots.Last(), genesisState!.LatestBlockHeader.ParentRoot, genesisState!.LatestBlockHeader.SigningRoot());
            TestContext.WriteLine("State 1 last block root: {0}, latest block header parent {1}, signing root {2}",
                block1State!.BlockRoots.Last(), block1State!.LatestBlockHeader.ParentRoot, block1State!.LatestBlockHeader.SigningRoot());
            TestContext.WriteLine("State 2 last block root: {0}, latest block header parent {1}, signing root {2}",
                block2State!.BlockRoots.Last(), block2State!.LatestBlockHeader.ParentRoot, block2State!.LatestBlockHeader.SigningRoot());
            TestContext.WriteLine("");

            TestContext.WriteLine("Genesis state last state root: {0}, last historical root {1}",
                genesisState!.StateRoots.LastOrDefault(), genesisState!.HistoricalRoots.LastOrDefault());
            TestContext.WriteLine("State 1 last state root: {0}, last historical root {1}",
                block1State!.StateRoots.Last(), block1State!.HistoricalRoots.LastOrDefault());
            TestContext.WriteLine("State 2 state last state root: {0}, last historical root {1}",
                block2State!.StateRoots.Last(), block2State!.HistoricalRoots.LastOrDefault());
            TestContext.WriteLine("Head state last state root: {0}, last historical root {1}",
                headState!.StateRoots.Last(), headState!.HistoricalRoots.LastOrDefault());
            TestContext.WriteLine("");

            BeaconBlockHeader state1LatestHeader = block1State!.LatestBlockHeader;
            TestContext.WriteLine(
                "State 1 latest header, slot: {0}, parent {1}, body root {2}, state root {3}, signature {4}, signing root {5}",
                state1LatestHeader.Slot, state1LatestHeader.ParentRoot, state1LatestHeader.BodyRoot,
                state1LatestHeader.StateRoot, state1LatestHeader.Signature,
                state1LatestHeader.SigningRoot());
            TestContext.WriteLine(
                "-- compare to Block 1, slot: {0}, parent {1}, body root {2}, state root {3}, signature {4}, signing root {5}",
                block1!.Slot, block1!.ParentRoot, block1.Body.HashTreeRoot(miscellaneousParameters, maxOperationsPerBlock),
                block1!.StateRoot, block1!.Signature,
                block1!.SigningRoot(miscellaneousParameters, maxOperationsPerBlock));

            // Assert
            block2!.ParentRoot.ShouldBe(block1Root);

            Hash32 block1SigningRoot = block1!.SigningRoot(miscellaneousParameters, maxOperationsPerBlock);
            Hash32 block1HashTreeRoot = block1!.HashTreeRoot(miscellaneousParameters, maxOperationsPerBlock);
            
            block1SigningRoot.ShouldBe(block1Root);
            
            block1.ParentRoot.ToString().ShouldStartWith("0x7f4520eb");
            block1.StateRoot.ToString().ShouldStartWith("0x134ba7eb");
            block1SigningRoot.ToString().ShouldStartWith("0xcc21e696");
            block1State.LatestBlockHeader.BodyRoot.ToString().ShouldStartWith("0xaea12492");
        }
    }
}
