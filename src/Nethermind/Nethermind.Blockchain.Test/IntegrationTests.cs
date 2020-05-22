//  Copyright (c) 2018 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.Processing;
using Nethermind.Blockchain.Producers;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Rewards;
using Nethermind.Blockchain.Tracing;
using Nethermind.Blockchain.Validators;
using Nethermind.Consensus;
using Nethermind.Consensus.Ethash;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Specs;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.Evm;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using Nethermind.State;
using Nethermind.State.Repositories;
using Nethermind.Db.Blooms;
using Nethermind.TxPool;
using Nethermind.TxPool.Storages;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test
{
    public class IntegrationTests
    {
        [Test]
        [Explicit("This test is unpredictably failing on Travis and nowhere else")]
        // It takes dotCover to run it quite long, increased timeouts
        public async Task Can_process_mined_blocks()
        {
            int timeMultiplier = 1; // for debugging

            TimeSpan miningDelay = TimeSpan.FromMilliseconds(200 * timeMultiplier);

            /* logging & instrumentation */
//            OneLoggerLogManager logger = new OneLoggerLogManager(new SimpleConsoleLogger(true));
            ILogManager logManager = LimboLogs.Instance;
            ILogger logger = logManager.GetClassLogger();

            /* spec */
            FakeSealer sealer = new FakeSealer(miningDelay);

            RopstenSpecProvider specProvider = RopstenSpecProvider.Instance;

            /* state & storage */
            StateDb codeDb = new StateDb();
            StateDb stateDb = new StateDb();
            StateProvider stateProvider = new StateProvider(stateDb, codeDb, logManager);
            StorageProvider storageProvider = new StorageProvider(stateDb, stateProvider, logManager);
            StateReader stateReader = new StateReader(stateDb, codeDb, logManager);
            
            /* store & validation */

            EthereumEcdsa ecdsa = new EthereumEcdsa(specProvider.ChainId, logManager);
            MemColumnsDb<ReceiptsColumns> receiptsDb = new MemColumnsDb<ReceiptsColumns>();
            TxPool.TxPool txPool = new TxPool.TxPool(NullTxStorage.Instance, Timestamper.Default, ecdsa, specProvider, new TxPoolConfig(), stateProvider, logManager);
            IReceiptStorage receiptStorage = new PersistentReceiptStorage(receiptsDb, specProvider, new ReceiptsRecovery());
            var blockInfoDb = new MemDb();
            BlockTree blockTree = new BlockTree(new MemDb(), new MemDb(), blockInfoDb, new ChainLevelInfoRepository(blockInfoDb), specProvider, txPool, NullBloomStorage.Instance, logManager);
            ITimestamper timestamper = Timestamper.Default;
            DifficultyCalculator difficultyCalculator = new DifficultyCalculator(specProvider);
            HeaderValidator headerValidator = new HeaderValidator(blockTree, sealer, specProvider, logManager);
            OmmersValidator ommersValidator = new OmmersValidator(blockTree, headerValidator, logManager);
            TxValidator txValidator = new TxValidator(ChainId.Ropsten);
            BlockValidator blockValidator = new BlockValidator(txValidator, headerValidator, ommersValidator, specProvider, logManager);

            TestTransactionsGenerator generator = new TestTransactionsGenerator(txPool, ecdsa, TimeSpan.FromMilliseconds(50 * timeMultiplier), LimboLogs.Instance);
            generator.Start();
            
            /* blockchain processing */
            BlockhashProvider blockhashProvider = new BlockhashProvider(blockTree, LimboLogs.Instance);
            VirtualMachine virtualMachine = new VirtualMachine(stateProvider, storageProvider, blockhashProvider, specProvider, logManager);
            TransactionProcessor processor = new TransactionProcessor(specProvider, stateProvider, storageProvider, virtualMachine, logManager);
            RewardCalculator rewardCalculator = new RewardCalculator(specProvider);
            BlockProcessor blockProcessor = new BlockProcessor(specProvider, blockValidator, rewardCalculator,
                processor, stateDb, codeDb, stateProvider, storageProvider, txPool, receiptStorage, logManager);
            BlockchainProcessor blockchainProcessor = new BlockchainProcessor(blockTree, blockProcessor, new TxSignaturesRecoveryStep(ecdsa, NullTxPool.Instance, LimboLogs.Instance), logManager, false);

            /* load ChainSpec and init */
            ChainSpecLoader loader = new ChainSpecLoader(new EthereumJsonSerializer());
            string path = "chainspec.json";
            logManager.GetClassLogger().Info($"Loading ChainSpec from {path}");
            ChainSpec chainSpec = loader.Load(File.ReadAllText(path));
            foreach (var allocation in chainSpec.Allocations)
            {
                stateProvider.CreateAccount(allocation.Key, allocation.Value.Balance);
                if (allocation.Value.Code != null)
                {
                    Keccak codeHash = stateProvider.UpdateCode(allocation.Value.Code);
                    stateProvider.UpdateCodeHash(allocation.Key, codeHash, specProvider.GenesisSpec);
                }
            }

            stateProvider.Commit(specProvider.GenesisSpec);
            chainSpec.Genesis.Header.StateRoot = stateProvider.StateRoot;
            chainSpec.Genesis.Header.Hash = chainSpec.Genesis.Header.CalculateHash();
            if (chainSpec.Genesis.Hash != new Keccak("0xafbc3c327d2d18ff2b843e89226ef288fcee379542f854f982e4cfb85916d126")) throw new Exception("Unexpected genesis hash");

            /* start processing */
            blockTree.SuggestBlock(chainSpec.Genesis);
            blockchainProcessor.Start();

            var transactionSelector = new TxPoolTxSource(txPool, stateReader, logManager);
            MinedBlockProducer minedBlockProducer = new MinedBlockProducer(transactionSelector, blockchainProcessor, sealer, blockTree, blockchainProcessor, stateProvider, timestamper, LimboLogs.Instance, difficultyCalculator);
            minedBlockProducer.Start();

            ManualResetEventSlim manualResetEvent = new ManualResetEventSlim(false);

            blockTree.NewHeadBlock += (sender, args) =>
            {
                if (args.Block.Number == 6) manualResetEvent.Set();
            };

            manualResetEvent.Wait(miningDelay * 100);
            await minedBlockProducer.StopAsync();

            int previousCount = 0;
            int totalTx = 0;
            for (int i = 0; i < 6; i++)
            {
                Block block = blockTree.FindBlock(i, BlockTreeLookupOptions.None);
                Assert.That(block, Is.Not.Null, $"Block {i} not produced");
                logger.Info($"Block {i} with {block.Transactions.Length} txs");

                ManualResetEventSlim blockProcessedEvent = new ManualResetEventSlim(false);
                blockchainProcessor.ProcessingQueueEmpty += (sender, args) => blockProcessedEvent.Set();
                blockchainProcessor.Enqueue(block, ProcessingOptions.ForceProcessing | ProcessingOptions.StoreReceipts | ProcessingOptions.ReadOnlyChain);
                blockProcessedEvent.Wait(miningDelay);

                GethStyleTracer gethStyleTracer = new GethStyleTracer(blockchainProcessor, receiptStorage, blockTree);

                int currentCount = receiptsDb.Keys.Count;
                logger.Info($"Current count of receipts {currentCount}");
                logger.Info($"Previous count of receipts {previousCount}");

                if (block.Transactions.Length > 0)
                {
                    Assert.AreNotEqual(previousCount, currentCount, $"receipts at block {i}");
                    totalTx += block.Transactions.Length;
                }

                previousCount = currentCount;
            }

            Assert.AreNotEqual(0, totalTx, "no tx in blocks");
        }
    }
}