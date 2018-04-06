using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using Nethermind.Blockchain.Difficulty;
using Nethermind.Blockchain.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Specs.ChainSpec;
using Nethermind.Evm;
using Nethermind.Mining;
using Nethermind.Store;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test
{
    [TestFixture]
    [Category("INTEGRATION")]
    public class PrivateChainTests
    {
        [Test]
        public void _Build_some_chain()
        {
            ILogger logger = new ConsoleLogger();
           
            ISealEngine sealEngine = new EthashSealEngine(new Ethash());
            ISpecProvider specProvider = RopstenSpecProvider.Instance;
            IReleaseSpec dynamicSpecForVm = new DynamicReleaseSpec(RopstenSpecProvider.Instance);
            
            IBlockStore blockStore = new BlockStore();
            IDifficultyCalculator difficultyCalculator = new DifficultyCalculator(specProvider); // dynamic spec here will be broken
            IHeaderValidator headerValidator = new HeaderValidator(difficultyCalculator, blockStore, sealEngine, specProvider, logger);
            IOmmersValidator ommersValidator = new OmmersValidator(blockStore, headerValidator, logger);
            IReleaseSpec currentSpec = RopstenSpecProvider.Instance.GetCurrentSpec();
            ITransactionValidator transactionValidator = new TransactionValidator(currentSpec, new SignatureValidator(currentSpec, ChainId.Ropsten));
            IBlockValidator blockValidator = new BlockValidator(transactionValidator, headerValidator, ommersValidator, logger);

            IBlockhashProvider blockhashProvider = new BlockhashProvider(blockStore);
            IDbProvider storageDbProvider = new DbProvider(logger);
            InMemoryDb codeDb = new InMemoryDb();
            InMemoryDb stateDb = new InMemoryDb();
            StateTree stateTree = new StateTree(stateDb);
            IStateProvider stateProvider = new StateProvider(stateTree, dynamicSpecForVm, logger, codeDb);
            IStorageProvider storageProvider = new StorageProvider(storageDbProvider, stateProvider, logger);
            
            IRewardCalculator rewardCalculator = new RewardCalculator(specProvider);
            IVirtualMachine virtualMachine = new VirtualMachine(dynamicSpecForVm, stateProvider, storageProvider, blockhashProvider, logger);
            IEthereumSigner ethereumSigner = new EthereumSigner(dynamicSpecForVm, ChainId.Ropsten);
            ITransactionProcessor processor = new TransactionProcessor(dynamicSpecForVm, stateProvider, storageProvider, virtualMachine, ethereumSigner, logger);
            ITransactionStore transactionStore = new TransactionStore();
            IBlockProcessor blockProcessor = new BlockProcessor(RopstenSpecProvider.Instance, blockValidator, rewardCalculator, processor, storageDbProvider, stateProvider, storageProvider, transactionStore, logger);
            IBlockchainProcessor blockchainProcessor = new BlockchainProcessor(blockProcessor, blockStore, transactionStore, difficultyCalculator, sealEngine, logger);
            
            ChainSpecLoader loader = new ChainSpecLoader(new UnforgivingJsonSerializer());
            string path = Path.Combine(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"..\..\..\..\Chains", "ropsten.json"));
            logger.Log($"Loading ChainSpec from {path}");
            ChainSpec chainSpec = loader.Load(File.ReadAllBytes(path));
            foreach (KeyValuePair<Address, BigInteger> allocation in chainSpec.Allocations)
            {
                stateProvider.CreateAccount(allocation.Key, allocation.Value);
            }
            
            stateProvider.Commit();
            chainSpec.Genesis.Header.StateRoot = stateProvider.StateRoot;
            chainSpec.Genesis.Header.RecomputeHash();
            if (chainSpec.Genesis.Hash != new Keccak("0x41941023680923e0fe4d74a34bdac8141f2540e3ae90623718e47d66d1ca4a2d"))
            {
                throw new Exception("Unexpected genesis hash");
            }
            
            blockchainProcessor.Process(chainSpec.Genesis);
        }
    }
}