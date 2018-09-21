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
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.Difficulty;
using Nethermind.Blockchain.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Logging;
using Nethermind.Core.Specs;
using Nethermind.Core.Specs.ChainSpec;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Evm;
using Nethermind.Store;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test
{
    [TestFixture]
    public class BlockchainProcessorTests
    {
        [Test]
        public async Task Test()
        {
            TimeSpan miningDelay = TimeSpan.FromMilliseconds(50);
            
            /* logging & instrumentation */
            var logger = new OneLoggerLogManager(new SimpleConsoleLogger(true));

            /* spec */
            var sealEngine = new FakeSealEngine(miningDelay);
            sealEngine.IsMining = true;
            
            var specProvider = RopstenSpecProvider.Instance;

            /* store & validation */
            var blockTree = new BlockTree(new MemDb(), new MemDb(), new MemDb(), specProvider, logger);
            var difficultyCalculator = new DifficultyCalculator(specProvider);
            var headerValidator = new HeaderValidator(difficultyCalculator, blockTree, sealEngine, specProvider, logger);
            var ommersValidator = new OmmersValidator(blockTree, headerValidator, logger);
            var transactionValidator = new TransactionValidator(new SignatureValidator(ChainId.Ropsten));
            var blockValidator = new BlockValidator(transactionValidator, headerValidator, ommersValidator, specProvider, logger);

            /* state & storage */
            var codeDb = new MemDb();
            var stateDb = new MemDb();
            var stateTree = new StateTree(stateDb);
            var stateProvider = new StateProvider(stateTree, codeDb, logger);
            var storageDbProvider = new MemDbProvider(logger);
            var storageProvider = new StorageProvider(storageDbProvider, stateProvider, logger);

            /* blockchain processing */
            var ethereumSigner = new EthereumSigner(specProvider, logger);
            var transactionStore = new TransactionStore();
            var blockhashProvider = new BlockhashProvider(blockTree);
            var virtualMachine = new VirtualMachine(stateProvider, storageProvider, blockhashProvider, logger);
            var processor = new TransactionProcessor(specProvider, stateProvider, storageProvider, virtualMachine, NullTracer.Instance, logger);
            var rewardCalculator = new RewardCalculator(specProvider);
            var blockProcessor = new BlockProcessor(specProvider, blockValidator, rewardCalculator, processor, storageDbProvider, stateProvider, storageProvider, transactionStore, logger);
            var blockchainProcessor = new BlockchainProcessor(blockTree, sealEngine, transactionStore, difficultyCalculator, blockProcessor, ethereumSigner, logger, new PerfService(NullLogManager.Instance));

            /* load ChainSpec and init */
            ChainSpecLoader loader = new ChainSpecLoader(new UnforgivingJsonSerializer());
            string path = Path.Combine(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"..\..\..\..\Chains", "ropsten.json"));
            logger.GetClassLogger().Info($"Loading ChainSpec from {path}");
            ChainSpec chainSpec = loader.Load(File.ReadAllBytes(path));
            foreach (KeyValuePair<Address, UInt256> allocation in chainSpec.Allocations)
            {
                stateProvider.CreateAccount(allocation.Key, allocation.Value);
            }

            stateProvider.Commit(specProvider.GenesisSpec);
            chainSpec.Genesis.Header.StateRoot = stateProvider.StateRoot; // TODO: shall it be HeaderSpec and not BlockHeader?
            chainSpec.Genesis.Header.Hash = BlockHeader.CalculateHash(chainSpec.Genesis.Header);
            if (chainSpec.Genesis.Hash != new Keccak("0x41941023680923e0fe4d74a34bdac8141f2540e3ae90623718e47d66d1ca4a2d"))
            {
                throw new Exception("Unexpected genesis hash");
            }

            /* start processing */
            blockTree.SuggestBlock(chainSpec.Genesis);
            blockchainProcessor.Start();
            
            ManualResetEvent manualResetEvent = new ManualResetEvent(false);

            blockTree.NewHeadBlock += (sender, args) =>
            {
                if (args.Block.Number == 6) manualResetEvent.Set();
            };

            manualResetEvent.WaitOne(miningDelay * 12);
            
            await blockchainProcessor.StopAsync(true).ContinueWith(
                t =>
                {
                    if (t.IsFaulted)
                    {
                        throw t.Exception;
                    }

                    Assert.GreaterOrEqual((int)blockTree.Head.Number, 6);
                });
        }
    }
}