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
using System.Xml;
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
            /* logging & instrumentation */
            var logger = new ConsoleLogger();

            /* spec */
            var blockMiningTime = TimeSpan.FromMilliseconds(50);
            var sealEngine = new FakeSealEngine(blockMiningTime);
            var specProvider = RopstenSpecProvider.Instance;
            var currentSpec = RopstenSpecProvider.Instance.GetCurrentSpec();

            /* stoira & validation */
            var blockStore = new BlockStore();
            var difficultyCalculator = new DifficultyCalculator(specProvider); // dynamic spec here will be broken
            var headerValidator = new HeaderValidator(difficultyCalculator, blockStore, sealEngine, specProvider, logger);
            var ommersValidator = new OmmersValidator(blockStore, headerValidator, logger);
            var transactionValidator = new TransactionValidator(new SignatureValidator(ChainId.Ropsten));
            var blockValidator = new BlockValidator(transactionValidator, headerValidator, ommersValidator, specProvider, logger);

            /* state & storage */
            var codeDb = new InMemoryDb();
            var stateDb = new InMemoryDb();
            var stateTree = new StateTree(stateDb);
            var stateProvider = new StateProvider(stateTree, logger, codeDb); // TODO: remove dynamic spec here
            var storageDbProvider = new DbProvider(logger);
            var storageProvider = new StorageProvider(storageDbProvider, stateProvider, logger);

            /* blockchain processing */
            var ethereumSigner = new EthereumSigner(specProvider, logger);
            var transactionStore = new TransactionStore();
            var blockchain = new BlockTree();
            var blockhashProvider = new BlockhashProvider(blockchain);
            var virtualMachine = new VirtualMachine(specProvider, stateProvider, storageProvider, blockhashProvider, logger); // TODO: remove dynamic spec here
            var processor = new TransactionProcessor(specProvider, stateProvider, storageProvider, virtualMachine, ethereumSigner, logger); // TODO: remove dynamic spec here
            var rewardCalculator = new RewardCalculator(specProvider);
            var blockProcessor = new BlockProcessor(specProvider, blockValidator, rewardCalculator, processor, storageDbProvider, stateProvider, storageProvider, transactionStore, logger);
            var blockchainProcessor = new BlockchainProcessor(blockchain, sealEngine, transactionStore, difficultyCalculator, blockProcessor, logger);

            /* load ChainSpec and init */
            ChainSpecLoader loader = new ChainSpecLoader(new UnforgivingJsonSerializer());
            string path = Path.Combine(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"..\..\..\..\Chains", "ropsten.json"));
            logger.Log($"Loading ChainSpec from {path}");
            ChainSpec chainSpec = loader.Load(File.ReadAllBytes(path));
            chainSpec.Genesis.Header.StateRoot = stateProvider.StateRoot; // TODO: shall it be HeaderSpec and not BlockHeader?
            chainSpec.Genesis.Header.RecomputeHash(); // TODO: shall it be HeaderSpec and not BlockHeader?
            if (chainSpec.Genesis.Hash != new Keccak("0x41941023680923e0fe4d74a34bdac8141f2540e3ae90623718e47d66d1ca4a2d"))
            {
                throw new Exception("Unexpected genesis hash");
            }

            foreach (KeyValuePair<Address, BigInteger> allocation in chainSpec.Allocations)
            {
                stateProvider.CreateAccount(allocation.Key, allocation.Value);
            }

            stateProvider.Commit(specProvider.GetGenesisSpec());

            /* start processing */
            sealEngine.IsMining = true; // TODO: start / stop?
            var generator = new FakeTransactionsGenerator(transactionStore, ethereumSigner, blockMiningTime, logger);
            generator.Start();
            blockchainProcessor.Start(chainSpec.Genesis);
            var roughlyNumberOfBlocks = 6;
            Thread.Sleep(blockMiningTime * (int)roughlyNumberOfBlocks);
            await blockchainProcessor.StopAsync(false);
            Assert.GreaterOrEqual(blockchainProcessor.HeadBlock.Number, roughlyNumberOfBlocks - 2, "number of blocks");
            Assert.GreaterOrEqual(blockchainProcessor.TotalTransactions, roughlyNumberOfBlocks - 2, "number of transactions");
        }
    }
}