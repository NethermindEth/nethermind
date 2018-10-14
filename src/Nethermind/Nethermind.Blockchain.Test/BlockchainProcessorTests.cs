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
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Logging;
using Nethermind.Core.Specs;
using Nethermind.Core.Specs.ChainSpec;
using Nethermind.Evm;
using Nethermind.Mining;
using Nethermind.Mining.Difficulty;
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
            OneLoggerLogManager logger = new OneLoggerLogManager(new SimpleConsoleLogger(true));

            /* spec */
            FakeSealEngine sealEngine = new FakeSealEngine(miningDelay);
            sealEngine.IsMining = true;

            RopstenSpecProvider specProvider = RopstenSpecProvider.Instance;

            /* store & validation */
            MemDb receiptsDb = new MemDb();
            TransactionStore transactionStore = new TransactionStore(receiptsDb, specProvider);
            BlockTree blockTree = new BlockTree(new MemDb(), new MemDb(), specProvider, transactionStore, logger);
            DifficultyCalculator difficultyCalculator = new DifficultyCalculator(specProvider);
            HeaderValidator headerValidator = new HeaderValidator(blockTree, sealEngine, specProvider, logger);
            OmmersValidator ommersValidator = new OmmersValidator(blockTree, headerValidator, logger);
            TransactionValidator transactionValidator = new TransactionValidator(new SignatureValidator(ChainId.Ropsten));
            BlockValidator blockValidator = new BlockValidator(transactionValidator, headerValidator, ommersValidator, specProvider, logger);

            /* state & storage */
            StateDb codeDb = new StateDb();
            StateDb stateDb = new StateDb();
            StateTree stateTree = new StateTree(stateDb);
            StateProvider stateProvider = new StateProvider(stateTree, codeDb, logger);
            StorageProvider storageProvider = new StorageProvider(stateDb, stateProvider, logger);

            /* blockchain processing */
            EthereumSigner ethereumSigner = new EthereumSigner(specProvider, logger);

            TestTransactionsGenerator generator = new TestTransactionsGenerator(transactionStore, new EthereumSigner(specProvider, NullLogManager.Instance), TimeSpan.FromMilliseconds(5), NullLogManager.Instance);
            generator.Start();

            BlockhashProvider blockhashProvider = new BlockhashProvider(blockTree);
            VirtualMachine virtualMachine = new VirtualMachine(stateProvider, storageProvider, blockhashProvider, logger);
            TransactionProcessor processor = new TransactionProcessor(specProvider, stateProvider, storageProvider, virtualMachine, logger);
            RewardCalculator rewardCalculator = new RewardCalculator(specProvider);
            BlockProcessor blockProcessor = new BlockProcessor(specProvider, blockValidator, rewardCalculator, processor, stateDb, codeDb, stateProvider, storageProvider, transactionStore, logger);
            BlockchainProcessor blockchainProcessor = new BlockchainProcessor(blockTree, blockProcessor, ethereumSigner, logger);

            /* load ChainSpec and init */
            ChainSpecLoader loader = new ChainSpecLoader(new UnforgivingJsonSerializer());
            string path = Path.Combine(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"..\..\..\..\Chains", "ropsten.json"));
            logger.GetClassLogger().Info($"Loading ChainSpec from {path}");
            ChainSpec chainSpec = loader.Load(File.ReadAllBytes(path));
            foreach (var allocation in chainSpec.Allocations) stateProvider.CreateAccount(allocation.Key, allocation.Value);

            stateProvider.Commit(specProvider.GenesisSpec);
            chainSpec.Genesis.Header.StateRoot = stateProvider.StateRoot; // TODO: shall it be HeaderSpec and not BlockHeader?
            chainSpec.Genesis.Header.Hash = BlockHeader.CalculateHash(chainSpec.Genesis.Header);
            if (chainSpec.Genesis.Hash != new Keccak("0x41941023680923e0fe4d74a34bdac8141f2540e3ae90623718e47d66d1ca4a2d")) throw new Exception("Unexpected genesis hash");

            /* start processing */
            blockTree.SuggestBlock(chainSpec.Genesis);
            blockchainProcessor.Start();
            
            MinedBlockProducer minedBlockProducer = new MinedBlockProducer(difficultyCalculator, transactionStore, blockchainProcessor, sealEngine, blockTree, NullLogManager.Instance);
            minedBlockProducer.Start();

            ManualResetEvent manualResetEvent = new ManualResetEvent(false);

            blockTree.NewHeadBlock += (sender, args) =>
            {
                if (args.Block.Number == 6) manualResetEvent.Set();
            };

            manualResetEvent.WaitOne(miningDelay * 12);

            await blockchainProcessor.StopAsync(true).ContinueWith(
                t =>
                {
                    if (t.IsFaulted) throw t.Exception;

                    Assert.GreaterOrEqual((int) blockTree.Head.Number, 6);
                });

            blockchainProcessor.AddTxData(blockTree.FindBlock(1));

            TxTracer tracer = new TxTracer(blockchainProcessor, transactionStore, blockTree);

            blockchainProcessor.Process(blockTree.FindBlock(1), ProcessingOptions.ForceProcessing | ProcessingOptions.StoreReceipts, NullTraceListener.Instance);
            Assert.AreNotEqual(0, receiptsDb.Keys.Count, "receipts");
            TransactionTrace trace = tracer.Trace(blockTree.FindBlock(1).Transactions[0].Hash);
            
            Assert.AreSame(TransactionTrace.QuickFail, trace);
        }
    }
}