using System;
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
using Nethermind.Core2.Cryptography.Ssz;
using Nethermind.Core2.Types;
using NSubstitute;
using Shouldly;
namespace Nethermind.BeaconNode.Test.Storage
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
            ICryptographyService cryptographyService = testServiceProvider.GetService<ICryptographyService>();
            IBeaconChainUtility beaconChainUtility = testServiceProvider.GetService<IBeaconChainUtility>();
            IForkChoice forkChoice = testServiceProvider.GetService<IForkChoice>();
            // Get genesis store initialise MemoryStoreProvider with the state
            IStore store = testServiceProvider.GetService<IStore>();
            await forkChoice.InitializeForkChoiceStoreAsync(store, state);            

            InitialValues initialValues = testServiceProvider.GetService<IOptions<InitialValues>>().Value;
            TimeParameters timeParameters = testServiceProvider.GetService<IOptions<TimeParameters>>().Value;
            SignatureDomains signatureDomains = testServiceProvider.GetService<IOptions<SignatureDomains>>().Value;

            // Move forward time
            ulong targetTime = 10 * 6; // part way into epoch 2
            for (ulong timeSinceGenesis = 1; timeSinceGenesis <= targetTime; timeSinceGenesis++)
            {
                ulong time = state.GenesisTime + timeSinceGenesis;
                await forkChoice.OnTickAsync(store, time);
                if (timeSinceGenesis % timeParameters.SecondsPerSlot == 0)
                {
                    BeaconBlock block = TestBlock.BuildEmptyBlockForNextSlot(testServiceProvider, state, BlsSignature.Zero);
                    SignedBeaconBlock signedBlock = TestState.StateTransitionAndSignBlock(testServiceProvider, state, block);
                    await forkChoice.OnBlockAsync(store, signedBlock);
                }
            }
            
            TestContext.WriteLine("");
            TestContext.WriteLine("***** State advanced to slot {0}, time {1}, ready to start tests *****", state.Slot, store.Time);
            TestContext.WriteLine("");
            
            // Act
            IStore retrievedStore = testServiceProvider.GetService<IStore>();
            retrievedStore.ShouldBeOfType(typeof(MemoryStore));

            Root headRoot = await forkChoice.GetHeadAsync(store);
            Root block2Root = await forkChoice.GetAncestorAsync(store, headRoot, new Slot(2));
            Root block1Root = await forkChoice.GetAncestorAsync(store, block2Root, Slot.One);
            Root genesisRoot = await forkChoice.GetAncestorAsync(store, block1Root, Slot.Zero);

            BeaconBlock headBlock = (await store.GetSignedBlockAsync(headRoot)).Message;
            BeaconBlock block2 = (await store.GetSignedBlockAsync(block2Root)).Message;
            BeaconBlock block1 = (await store.GetSignedBlockAsync(block1Root)).Message;
            BeaconBlock genesisBlock = (await store.GetSignedBlockAsync(genesisRoot)).Message;

            BeaconState headState= await store.GetBlockStateAsync(headRoot);
            BeaconState block2State = await store.GetBlockStateAsync(block2Root);
            BeaconState block1State = await store.GetBlockStateAsync(block1Root);
            BeaconState genesisState = await store.GetBlockStateAsync(genesisRoot);

            TestContext.WriteLine("Genesis lookup root: {0}", genesisRoot);
            TestContext.WriteLine("Block 1 lookup root: {0}", block1Root);
            TestContext.WriteLine("Block 2 lookup root: {0}", block2Root);
            TestContext.WriteLine("");

            Domain domain =
                beaconChainUtility.ComputeDomain(signatureDomains.BeaconProposer, initialValues.GenesisForkVersion);

            Root genesisHashTreeRoot = cryptographyService.HashTreeRoot(genesisBlock!);
            Root genesisSigningRoot = beaconChainUtility.ComputeSigningRoot(genesisHashTreeRoot, domain);
            Root block1HashTreeRoot = cryptographyService.HashTreeRoot(block1!);
            Root block1SigningRoot = beaconChainUtility.ComputeSigningRoot(block1HashTreeRoot, domain);
            Root block2HashTreeRoot = cryptographyService.HashTreeRoot(block2!);
            Root block2SigningRoot = beaconChainUtility.ComputeSigningRoot(block2HashTreeRoot, domain);

            TestContext.WriteLine("Genesis sign root: {0}, hash root: {1}", genesisSigningRoot, genesisHashTreeRoot);
            TestContext.WriteLine("Block 1 sign root: {0}, hash root: {1}", block1SigningRoot, block1HashTreeRoot);
            TestContext.WriteLine("Block 2 sign root: {0}, hash root: {1}", block2SigningRoot, block2HashTreeRoot);
            TestContext.WriteLine("");

            TestContext.WriteLine("Genesis state root: {0}", genesisBlock!.StateRoot);
            TestContext.WriteLine("Block state 1 root: {0}", block1!.StateRoot);
            TestContext.WriteLine("Block state 2 root: {0}", block2!.StateRoot);
            TestContext.WriteLine("");

            TestContext.WriteLine("Genesis state hash root: {0}",
                cryptographyService.HashTreeRoot(genesisState!));
            TestContext.WriteLine("State 1 hash root: {0}",
                cryptographyService.HashTreeRoot(block1State!));
            TestContext.WriteLine("State 2 hash root: {0}",
                cryptographyService.HashTreeRoot(block2State!));
            TestContext.WriteLine("");

            TestContext.WriteLine("Genesis state last block root: {0}, latest block header parent {1}, hash tree root {2}",
                genesisState!.BlockRoots.Last(), genesisState!.LatestBlockHeader.ParentRoot, 
                cryptographyService.HashTreeRoot(genesisState!.LatestBlockHeader));
            TestContext.WriteLine("State 1 last block root: {0}, latest block header parent {1}, hash tree root {2}",
                block1State!.BlockRoots.Last(), block1State!.LatestBlockHeader.ParentRoot, 
                cryptographyService.HashTreeRoot(block1State!.LatestBlockHeader));
            TestContext.WriteLine("State 2 last block root: {0}, latest block header parent {1}, hash tree root {2}",
                block2State!.BlockRoots.Last(), block2State!.LatestBlockHeader.ParentRoot, 
                cryptographyService.HashTreeRoot(block2State!.LatestBlockHeader));
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
                "State 1 latest header, slot: {0}, parent {1}, body root {2}, state root {3}, signature {4}, header hash tree root (without state) {5}",
                state1LatestHeader.Slot, state1LatestHeader.ParentRoot, state1LatestHeader.BodyRoot,
                state1LatestHeader.StateRoot, state1LatestHeader,
                cryptographyService.HashTreeRoot(state1LatestHeader));
            
            // NOTE: cryptographyService.HashTreeRoot(beaconBlockHeader) == cryptographyService.HashTreeRoot(beaconBlock)
            //       likewise, the ComputeSigningRoot() of both will be the same
            
            Root block1BodyHashTreeRootResult = cryptographyService.HashTreeRoot(block1!.Body);
            Root block1HashTreeRootResult = cryptographyService.HashTreeRoot(block1!);
            Root block1SigningRootResult = beaconChainUtility.ComputeSigningRoot(block1HashTreeRootResult, domain);
//            Hash32 block1HashTreeRootResult = cryptographyService.HashTreeRoot(block1!,
//                maxOperationsPerBlock.MaximumProposerSlashings,
//                maxOperationsPerBlock.MaximumAttesterSlashings, maxOperationsPerBlock.MaximumAttestations,
//                maxOperationsPerBlock.MaximumDeposits, maxOperationsPerBlock.MaximumVoluntaryExits,
//                miscellaneousParameters.MaximumValidatorsPerCommittee);

            TestContext.WriteLine(
                "-- compare to Block 1, slot: {0}, parent {1}, body root {2}, state root {3}, hash tree root {4}, signing root {5}",
                block1!.Slot, block1!.ParentRoot, block1BodyHashTreeRootResult,
                block1!.StateRoot, 
                block1HashTreeRootResult,
                block1SigningRootResult);

            // Assert
            block2!.ParentRoot.ShouldBe(block1Root);
            
            block1HashTreeRootResult.ShouldBe(block1Root);
            
            // These values just captured from output
            block1.ParentRoot.ToString().ShouldStartWith("0xc5ba9857");
            block1.StateRoot.ToString().ShouldStartWith("0xfadf79e4");
            block1HashTreeRootResult.ToString().ShouldStartWith("0xe29b9bf1");
            block1SigningRootResult.ToString().ShouldStartWith("0x11c26e7f");
            block1State.LatestBlockHeader.BodyRoot.ToString().ShouldStartWith("0x123ae93e");
        }
    }
}
