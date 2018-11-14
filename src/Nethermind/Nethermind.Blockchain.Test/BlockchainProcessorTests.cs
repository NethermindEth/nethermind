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
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.TransactionPools;
using Nethermind.Blockchain.TransactionPools.Storages;
using Nethermind.Blockchain.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Logging;
using Nethermind.Core.Specs;
using Nethermind.Core.Specs.ChainSpec;
using Nethermind.Core.Test.Builders;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Mining;
using Nethermind.Mining.Difficulty;
using Nethermind.Store;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test
{
    [TestFixture]
    public class BlockchainProcessorTests
    {
        [Test]
        public async Task Can_process_mined_blocks()
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

        private class ProcessingTestContext
        {
            private BlockTree _blockTree;
            private AutoResetEvent _resetEvent;
            
            public ProcessingTestContext()
            {
                MemDb blockDb = new MemDb();
                MemDb blockInfoDb = new MemDb();
                _blockTree = new BlockTree(blockDb, blockInfoDb, MainNetSpecProvider.Instance, NullTransactionPool.Instance, NullLogManager.Instance);
                IBlockProcessor blockProcessor = Substitute.For<IBlockProcessor>();
                BlockchainProcessor processor = new BlockchainProcessor(_blockTree, blockProcessor, NullRecoveryStep.Instance, NullLogManager.Instance, false);
                _resetEvent = new AutoResetEvent(false);
                bool ignoreNextSignal = true;
                processor.ProcessingQueueEmpty += (sender, args) =>
                {
                    if (ignoreNextSignal)
                    {
                        ignoreNextSignal = false;
                        return;
                    }
                
                    _resetEvent.Set();
                };

                blockProcessor.Process(Arg.Any<Keccak>(), Arg.Any<Block[]>(), ProcessingOptions.None, NullTraceListener.Instance).Returns(ci => ci.ArgAt<Block[]>(1));
                processor.Start();
            }

            public class AfterBlock
            {
                private readonly ProcessingTestContext _processingTestContext;
                private readonly Block _block;
                private readonly BlockHeader _headBefore;

                private const int ProcessingWait = 1000;
                private const int IgnoreWait = 200;
                
                public AfterBlock(ProcessingTestContext processingTestContext, Block block)
                {
                    _processingTestContext = processingTestContext;
                    _block = block;

                    _headBefore = _processingTestContext._blockTree.Head;
                    _processingTestContext._blockTree.SuggestBlock(_block);
                }

                public ProcessingTestContext BecomesGenesis()
                {
                    _processingTestContext._resetEvent.WaitOne(ProcessingWait);
                    Assert.AreEqual(_block.Header, _processingTestContext._blockTree.Genesis, "genesis");
                    return _processingTestContext;
                }
                
                public ProcessingTestContext BecomesNewHead()
                {
                    _processingTestContext._resetEvent.WaitOne(ProcessingWait);
                    Assert.AreEqual(_block.Header, _processingTestContext._blockTree.Head, "head");
                    return _processingTestContext;
                }
                
                public ProcessingTestContext IsKeptOnBranch()
                {
                    _processingTestContext._resetEvent.WaitOne(IgnoreWait);
                    Assert.AreEqual(_headBefore, _processingTestContext._blockTree.Head, "head");
                    return _processingTestContext;
                }
            }
            
            public AfterBlock Then(Block block)
            {
                return new AfterBlock(this, block);
            }
        }

        private static class When
        {
            public static ProcessingTestContext ProcessingBlocks => new ProcessingTestContext();
        }
        
        private static Block _block0 = Build.A.Block.WithNumber(0).WithNonce(0).WithDifficulty(0).TestObject;
        private static Block _block1D2 = Build.A.Block.WithNumber(1).WithNonce(1).WithParent(_block0).WithDifficulty(2).TestObject;
        private static Block _block2D4 = Build.A.Block.WithNumber(2).WithNonce(2).WithParent(_block1D2).WithDifficulty(2).TestObject;
        private static Block _block3D6 = Build.A.Block.WithNumber(3).WithNonce(3).WithParent(_block2D4).WithDifficulty(2).TestObject;
        private static Block _block4D8 = Build.A.Block.WithNumber(4).WithNonce(4).WithParent(_block3D6).WithDifficulty(2).TestObject;
        private static Block _block5D10 = Build.A.Block.WithNumber(5).WithNonce(5).WithParent(_block4D8).WithDifficulty(2).TestObject;
        private static Block _blockB2D4 = Build.A.Block.WithNumber(2).WithNonce(6).WithParent(_block1D2).WithDifficulty(2).TestObject;
        private static Block _blockB3D8 = Build.A.Block.WithNumber(3).WithNonce(7).WithParent(_blockB2D4).WithDifficulty(4).TestObject;
        private static Block _blockC2D100 = Build.A.Block.WithNumber(3).WithNonce(8).WithParent(_block1D2).WithDifficulty(98).TestObject;
        private static Block _blockD2D200 = Build.A.Block.WithNumber(3).WithNonce(8).WithParent(_block1D2).WithDifficulty(198).TestObject;
        private static Block _blockE2D300 = Build.A.Block.WithNumber(3).WithNonce(8).WithParent(_block1D2).WithDifficulty(298).TestObject;
        
        [Test]
        public void Can_process_sequence()
        {
            When.ProcessingBlocks
                .Then(_block0).BecomesGenesis()
                .Then(_block1D2).BecomesNewHead()
                .Then(_block2D4).BecomesNewHead()
                .Then(_block3D6).BecomesNewHead()
                .Then(_block4D8).BecomesNewHead();
        }
        
        [Test]
        public void Can_ignore_lower_difficulty()
        {
            When.ProcessingBlocks
                .Then(_block0).BecomesGenesis()
                .Then(_block1D2).BecomesNewHead()
                .Then(_blockB2D4).BecomesNewHead()
                .Then(_blockB3D8).BecomesNewHead()
                .Then(_block2D4).IsKeptOnBranch()
                .Then(_block3D6).IsKeptOnBranch();
        }
        
        [Test]
        public void Can_ignore_same_difficulty()
        {
            When.ProcessingBlocks
                .Then(_block0).BecomesGenesis()
                .Then(_block1D2).BecomesNewHead()
                .Then(_block2D4).BecomesNewHead()
                .Then(_blockB2D4).IsKeptOnBranch();
        }
        
        [Test]
        public void Can_reorganize_to_same_length()
        {
            When.ProcessingBlocks
                .Then(_block0).BecomesGenesis()
                .Then(_block1D2).BecomesNewHead()
                .Then(_block2D4).BecomesNewHead()
                .Then(_block3D6).BecomesNewHead()
                .Then(_blockB2D4).IsKeptOnBranch()
                .Then(_blockB3D8).BecomesNewHead();
        }
        
        [Test]
        public void Can_reorganize_there_and_back()
        {
            When.ProcessingBlocks
                .Then(_block0).BecomesGenesis()
                .Then(_block1D2).BecomesNewHead()
                .Then(_block2D4).BecomesNewHead()
                .Then(_block3D6).BecomesNewHead()
                .Then(_blockB2D4).IsKeptOnBranch()
                .Then(_blockB3D8).BecomesNewHead()
                .Then(_block4D8).IsKeptOnBranch()
                .Then(_block5D10).BecomesNewHead();
        }
        
        [Test]
        public void Can_reorganize_to_longer_path()
        {
            When.ProcessingBlocks
                .Then(_block0).BecomesGenesis()
                .Then(_block1D2).BecomesNewHead()
                .Then(_blockB2D4).BecomesNewHead()
                .Then(_blockB3D8).BecomesNewHead()
                .Then(_block2D4).IsKeptOnBranch()
                .Then(_block3D6).IsKeptOnBranch()
                .Then(_block4D8).IsKeptOnBranch()
                .Then(_block5D10).BecomesNewHead();
        }
        
        [Test]
        public void Can_reorganize_to_shorter_path()
        {
            When.ProcessingBlocks
                .Then(_block0).BecomesGenesis()
                .Then(_block1D2).BecomesNewHead()
                .Then(_block2D4).BecomesNewHead()
                .Then(_block3D6).BecomesNewHead()
                .Then(_blockC2D100).BecomesNewHead();
        }
        
        [Test]
        public void Can_reorganize_just_head_block_twice()
        {
            When.ProcessingBlocks
                .Then(_block0).BecomesGenesis()
                .Then(_block1D2).BecomesNewHead()
                .Then(_block2D4).BecomesNewHead()
                .Then(_blockC2D100).BecomesNewHead()
                .Then(_blockD2D200).BecomesNewHead()
                .Then(_blockE2D300).BecomesNewHead();
        }
    }
}