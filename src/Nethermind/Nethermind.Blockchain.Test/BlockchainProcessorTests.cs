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
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.TransactionPools;
using Nethermind.Blockchain.TransactionPools.Storages;
using Nethermind.Blockchain.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Logging;
using Nethermind.Core.Specs;
using Nethermind.Core.Specs.ChainSpec;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
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
            int timeMultiplier = 1; // for debugging
            
            TimeSpan miningDelay = TimeSpan.FromMilliseconds(50 * timeMultiplier);

            /* logging & instrumentation */
            OneLoggerLogManager logger = new OneLoggerLogManager(new SimpleConsoleLogger(true));

            /* spec */
            FakeSealEngine sealEngine = new FakeSealEngine(miningDelay);
            sealEngine.IsMining = true;

            RopstenSpecProvider specProvider = RopstenSpecProvider.Instance;

            /* store & validation */
            
            EthereumSigner ethereumSigner = new EthereumSigner(specProvider, NullLogManager.Instance);
            MemDb receiptsDb = new MemDb();
            TransactionPool transactionPool = new TransactionPool(new NullTransactionStorage(),
                new PendingTransactionThresholdValidator(), new Timestamp(), ethereumSigner, logger);
            IReceiptStorage receiptStorage = new PersistentReceiptStorage(receiptsDb, specProvider);
            BlockTree blockTree = new BlockTree(new MemDb(), new MemDb(), specProvider, transactionPool, logger);
            Timestamp timestamp = new Timestamp();
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

            TestTransactionsGenerator generator = new TestTransactionsGenerator(transactionPool, ethereumSigner, TimeSpan.FromMilliseconds(5 * timeMultiplier), NullLogManager.Instance);
            generator.Start();

            /* blockchain processing */
            BlockhashProvider blockhashProvider = new BlockhashProvider(blockTree);
            VirtualMachine virtualMachine = new VirtualMachine(stateProvider, storageProvider, blockhashProvider, logger);
            TransactionProcessor processor = new TransactionProcessor(specProvider, stateProvider, storageProvider, virtualMachine, logger);
            RewardCalculator rewardCalculator = new RewardCalculator(specProvider);
            BlockProcessor blockProcessor = new BlockProcessor(specProvider, blockValidator, rewardCalculator,
                processor, stateDb, codeDb, stateProvider, storageProvider, transactionPool, receiptStorage, logger);
            BlockchainProcessor blockchainProcessor = new BlockchainProcessor(blockTree, blockProcessor, new TxSignaturesRecoveryStep(ethereumSigner), logger, false);

            /* load ChainSpec and init */
            ChainSpecLoader loader = new ChainSpecLoader(new UnforgivingJsonSerializer());
            string path = "chainspec.json";
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
            
            MinedBlockProducer minedBlockProducer = new MinedBlockProducer(difficultyCalculator, transactionPool, blockchainProcessor, sealEngine, blockTree, timestamp, NullLogManager.Instance);
            minedBlockProducer.Start();

            ManualResetEvent manualResetEvent = new ManualResetEvent(false);

            blockTree.NewHeadBlock += (sender, args) =>
            {
                if (args.Block.Number == 6) manualResetEvent.Set();
            };

            manualResetEvent.WaitOne(miningDelay * 12 * timeMultiplier * 1000);
            await minedBlockProducer.StopAsync();
            
            int previousCount = 0;
            int totalTx = 0;
            for (int i = 0; i < 6; i++)
            {
                Block block = blockTree.FindBlock(new UInt256(i));
                Console.WriteLine($"Block {i} with {block.Transactions.Length} txs");
                
                ManualResetEvent blockProcessedEvent = new ManualResetEvent(false);
                blockchainProcessor.ProcessingQueueEmpty += (sender, args) => blockProcessedEvent.Set(); 
                blockchainProcessor.SuggestBlock(block.Hash, ProcessingOptions.ForceProcessing | ProcessingOptions.StoreReceipts | ProcessingOptions.ReadOnlyChain);
                blockProcessedEvent.WaitOne(1000);
                
                TxTracer tracer = new TxTracer(blockchainProcessor, receiptStorage, blockTree);

                int currentCount = receiptsDb.Keys.Count;
                Console.WriteLine($"Current count of receipts {currentCount}");
                Console.WriteLine($"Previous count of receipts {previousCount}");
                
                if (block.Transactions.Length > 0)
                {
                    TransactionTrace trace = tracer.Trace(block.Transactions[0].Hash);
                    Assert.AreSame(TransactionTrace.QuickFail, trace);
                    Assert.AreNotEqual(previousCount, currentCount, $"receipts at block {i}");
                    totalTx += block.Transactions.Length;
                }
                
                previousCount = currentCount;
            }
            
            Assert.AreNotEqual(0, totalTx, "no tx in blocks");
        }
    }
}