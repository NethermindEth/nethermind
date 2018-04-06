/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.Difficulty;
using Nethermind.Blockchain.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Specs.ChainSpec;
using Nethermind.Evm;
using Nethermind.Store;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test
{
    [TestFixture]
    [Category("INTEGRATION")]
    public class PrivateChainTests
    {
        [Test]
        public async Task Build_some_chain()
        {
            TimeSpan blockMiningTime = TimeSpan.FromMilliseconds(50);
            
            ILogger logger = new ConsoleLogger();
            ISealEngine sealEngine = new FakeSealEngine(blockMiningTime);
            ISpecProvider specProvider = RopstenSpecProvider.Instance;
            IReleaseSpec dynamicSpecForVm = new DynamicReleaseSpec(RopstenSpecProvider.Instance);
            IEthereumSigner ethereumSigner = new EthereumSigner(RopstenSpecProvider.Instance, logger); // dynamic spec here will be broken
            
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
            
            FakeTransactionsGenerator generator = new FakeTransactionsGenerator(transactionStore, ethereumSigner, blockMiningTime, logger);
            generator.Start();
            
            sealEngine.IsMining = true; // TODO: start / stop?

            BigInteger roughlyNumberOfBlocks = 6;
            blockchainProcessor.Start(chainSpec.Genesis);
            Thread.Sleep(blockMiningTime * (int)roughlyNumberOfBlocks);
            await blockchainProcessor.StopAsync();
            Assert.GreaterOrEqual(blockchainProcessor.HeadBlock.Number, roughlyNumberOfBlocks - 2, "number of blocks");
            Assert.GreaterOrEqual(blockchainProcessor.TotalTransactions, roughlyNumberOfBlocks - 2, "number of transactions");
        }
    }
}