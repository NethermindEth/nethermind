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
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Processing;
using Nethermind.Blockchain.Rewards;
using Nethermind.Blockchain.Test;
using Nethermind.Blockchain.Validators;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Evm.Tracing;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.Synchronization.BeamSync;
using Nethermind.Synchronization.ParallelSync;
using NSubstitute;
using NUnit.Framework;
using BlockTree = Nethermind.Blockchain.BlockTree;

namespace Nethermind.Synchronization.Test.BeamSync
{
    [Parallelizable(ParallelScope.Self)]
    [TestFixture]
    public class BeamBlockchainProcessorTests
    {
        private BlockTree _blockTree;
        private BlockValidator _validator;
        private IBlockProcessingQueue _blockchainProcessingQueue;
        private IBlockchainProcessor _blockchainProcessor;
        private BeamBlockchainProcessor _beamBlockchainProcessor;

        [SetUp]
        public void SetUp()
        {
            _blockchainProcessingQueue = Substitute.For<IBlockProcessingQueue>();
            _blockchainProcessor = Substitute.For<IBlockchainProcessor>();
            _blockTree = Build.A.BlockTree().OfChainLength(10).TestObject;
            HeaderValidator headerValidator = new HeaderValidator(_blockTree, NullSealEngine.Instance, MainnetSpecProvider.Instance, LimboLogs.Instance);
            _validator = new BlockValidator(Always.Valid, headerValidator, Always.Valid, MainnetSpecProvider.Instance, LimboLogs.Instance);
        }
        
        [TearDown]
        public void TearDown()
        {
            _beamBlockchainProcessor.Dispose();
            _blockchainProcessor.Dispose();
        }

        [Test, Retry(3)]
        public void Valid_block_makes_it_all_the_way()
        {
            SetupBeamProcessor();
            Block newBlock = Build.A.Block.WithParent(_blockTree.Head).WithTotalDifficulty(_blockTree.Head.TotalDifficulty + 1).TestObject;
            _blockTree.SuggestBlock(newBlock);
            Thread.Sleep(1000);
            // _blockchainProcessor.Received().Process(newBlock, ProcessingOptions.Beam, NullBlockTracer.Instance);
        }

        [Test, Retry(3)]
        public void Valid_block_with_transactions_makes_it_all_the_way()
        {
            SetupBeamProcessor();
            EthereumEcdsa ethereumEcdsa = new EthereumEcdsa(MainnetSpecProvider.Instance, LimboLogs.Instance);
            Block newBlock = Build.A.Block.WithParent(_blockTree.Head).WithReceiptsRoot(new Keccak("0xeb82c315eaf2c2a5dfc1766b075263d80e8b3ab9cb690d5304cdf114fff26939")).WithTransactions(Build.A.Transaction.SignedAndResolved(ethereumEcdsa, TestItem.PrivateKeyA, 10000000).TestObject, Build.A.Transaction.SignedAndResolved(ethereumEcdsa, TestItem.PrivateKeyB, 10000000).TestObject).WithGasUsed(42000).WithTotalDifficulty(_blockTree.Head.TotalDifficulty + 1).TestObject;
            _blockTree.SuggestBlock(newBlock);
            Thread.Sleep(1000);
            // _blockchainProcessor.Received().Process(newBlock, ProcessingOptions.Beam, NullBlockTracer.Instance);
        }
        
        [Test, Retry(3)]
        public void Valid_block_with_transactions_makes_it_is_processed_normally_if_beam_syncing_finished()
        {
            ISyncModeSelector syncModeSelector = Substitute.For<ISyncModeSelector>();
            SetupBeamProcessor(syncModeSelector);
            syncModeSelector.Preparing += Raise.EventWith(new SyncModeChangedEventArgs(SyncMode.Beam, SyncMode.Full));
            syncModeSelector.Changing += Raise.EventWith(new SyncModeChangedEventArgs(SyncMode.Beam, SyncMode.Full));
            syncModeSelector.Changed += Raise.EventWith(new SyncModeChangedEventArgs(SyncMode.Beam, SyncMode.Full));
            
            EthereumEcdsa ethereumEcdsa = new EthereumEcdsa(MainnetSpecProvider.Instance, LimboLogs.Instance);
            Block newBlock = Build.A.Block.WithParent(_blockTree.Head).WithReceiptsRoot(new Keccak("0xeb82c315eaf2c2a5dfc1766b075263d80e8b3ab9cb690d5304cdf114fff26939")).WithTransactions(Build.A.Transaction.SignedAndResolved(ethereumEcdsa, TestItem.PrivateKeyA, 10000000).TestObject, Build.A.Transaction.SignedAndResolved(ethereumEcdsa, TestItem.PrivateKeyB, 10000000).TestObject).WithGasUsed(42000).WithTotalDifficulty(_blockTree.Head.TotalDifficulty + 1).TestObject;
            _blockTree.SuggestBlock(newBlock);
            Thread.Sleep(1000);
            _blockchainProcessor.DidNotReceiveWithAnyArgs().Process(newBlock, ProcessingOptions.Beam, NullBlockTracer.Instance);
            _blockchainProcessingQueue.Received().Enqueue(newBlock, ProcessingOptions.StoreReceipts);
        }
        
        [Test, Retry(3)]
        public void Can_enqueue_previously_shelved()
        {
            ISyncModeSelector syncModeSelector = Substitute.For<ISyncModeSelector>();
            SetupBeamProcessor(syncModeSelector);

            EthereumEcdsa ethereumEcdsa = new EthereumEcdsa(MainnetSpecProvider.Instance, LimboLogs.Instance);
            Block newBlock0 = Build.A.Block.WithParent(_blockTree.Head).WithReceiptsRoot(new Keccak("0xeb82c315eaf2c2a5dfc1766b075263d80e8b3ab9cb690d5304cdf114fff26939")).WithTransactions(Build.A.Transaction.SignedAndResolved(ethereumEcdsa, TestItem.PrivateKeyA, 10000000).TestObject, Build.A.Transaction.SignedAndResolved(ethereumEcdsa, TestItem.PrivateKeyB, 10000000).TestObject).WithGasUsed(42000).WithTotalDifficulty(_blockTree.Head.TotalDifficulty + 1).TestObject;
            Block newBlock1 = Build.A.Block.WithParent(newBlock0.Header).WithReceiptsRoot(new Keccak("0xeb82c315eaf2c2a5dfc1766b075263d80e8b3ab9cb690d5304cdf114fff26939")).WithTransactions(Build.A.Transaction.SignedAndResolved(ethereumEcdsa, TestItem.PrivateKeyA, 10000000).TestObject, Build.A.Transaction.SignedAndResolved(ethereumEcdsa, TestItem.PrivateKeyB, 10000000).TestObject).WithGasUsed(42000).WithTotalDifficulty(_blockTree.Head.TotalDifficulty + 2).TestObject;
            Block newBlock2 = Build.A.Block.WithParent(newBlock1.Header).WithReceiptsRoot(new Keccak("0xeb82c315eaf2c2a5dfc1766b075263d80e8b3ab9cb690d5304cdf114fff26939")).WithTransactions(Build.A.Transaction.SignedAndResolved(ethereumEcdsa, TestItem.PrivateKeyA, 10000000).TestObject, Build.A.Transaction.SignedAndResolved(ethereumEcdsa, TestItem.PrivateKeyB, 10000000).TestObject).WithGasUsed(42000).WithTotalDifficulty(_blockTree.Head.TotalDifficulty + 3).TestObject;
            Block newBlock3 = Build.A.Block.WithParent(newBlock2.Header).WithReceiptsRoot(new Keccak("0xeb82c315eaf2c2a5dfc1766b075263d80e8b3ab9cb690d5304cdf114fff26939")).WithTransactions(Build.A.Transaction.SignedAndResolved(ethereumEcdsa, TestItem.PrivateKeyA, 10000000).TestObject, Build.A.Transaction.SignedAndResolved(ethereumEcdsa, TestItem.PrivateKeyB, 10000000).TestObject).WithGasUsed(42000).WithTotalDifficulty(_blockTree.Head.TotalDifficulty + 4).TestObject;
            Block newBlock4 = Build.A.Block.WithParent(newBlock3.Header).WithReceiptsRoot(new Keccak("0xeb82c315eaf2c2a5dfc1766b075263d80e8b3ab9cb690d5304cdf114fff26939")).WithTransactions(Build.A.Transaction.SignedAndResolved(ethereumEcdsa, TestItem.PrivateKeyA, 10000000).TestObject, Build.A.Transaction.SignedAndResolved(ethereumEcdsa, TestItem.PrivateKeyB, 10000000).TestObject).WithGasUsed(42000).WithTotalDifficulty(_blockTree.Head.TotalDifficulty + 5).TestObject;
            
            var args = new SyncModeChangedEventArgs(SyncMode.Beam, SyncMode.Full);
            _blockTree.SuggestBlock(newBlock0);
            syncModeSelector.Preparing += Raise.EventWith(args);
            _blockTree.SuggestBlock(newBlock1);
            syncModeSelector.Changing += Raise.EventWith(args);
            _blockTree.SuggestBlock(newBlock2);
            syncModeSelector.Changed += Raise.EventWith(args);
            _blockTree.SuggestBlock(newBlock3);
            syncModeSelector.Preparing += Raise.EventWith(new SyncModeChangedEventArgs(SyncMode.Beam, SyncMode.Full));
            _blockTree.SuggestBlock(newBlock4);
            
            Thread.Sleep(1000);
            // _blockchainProcessor.Received().Process(newBlock0, ProcessingOptions.Beam, NullBlockTracer.Instance);
            _blockchainProcessingQueue.Received().Enqueue(newBlock1, ProcessingOptions.StoreReceipts);
            _blockchainProcessingQueue.Received().Enqueue(newBlock2, ProcessingOptions.StoreReceipts);
            _blockchainProcessingQueue.Received().Enqueue(newBlock3, ProcessingOptions.StoreReceipts);
            _blockchainProcessingQueue.Received().Enqueue(newBlock4, ProcessingOptions.StoreReceipts);
        }
        
        [Test, Retry(3)]
        public void Will_ignore_transitions_other_than_full()
        {
            ISyncModeSelector syncModeSelector = Substitute.For<ISyncModeSelector>();
            SetupBeamProcessor(syncModeSelector);

            EthereumEcdsa ethereumEcdsa = new EthereumEcdsa(MainnetSpecProvider.Instance, LimboLogs.Instance);
            Block newBlock0 = Build.A.Block.WithParent(_blockTree.Head).WithReceiptsRoot(new Keccak("0xeb82c315eaf2c2a5dfc1766b075263d80e8b3ab9cb690d5304cdf114fff26939")).WithTransactions(Build.A.Transaction.SignedAndResolved(ethereumEcdsa, TestItem.PrivateKeyA, 10000000).TestObject, Build.A.Transaction.SignedAndResolved(ethereumEcdsa, TestItem.PrivateKeyB, 10000000).TestObject).WithGasUsed(42000).WithTotalDifficulty(_blockTree.Head.TotalDifficulty + 1).TestObject;
            Block newBlock1 = Build.A.Block.WithParent(newBlock0.Header).WithReceiptsRoot(new Keccak("0xeb82c315eaf2c2a5dfc1766b075263d80e8b3ab9cb690d5304cdf114fff26939")).WithTransactions(Build.A.Transaction.SignedAndResolved(ethereumEcdsa, TestItem.PrivateKeyA, 10000000).TestObject, Build.A.Transaction.SignedAndResolved(ethereumEcdsa, TestItem.PrivateKeyB, 10000000).TestObject).WithGasUsed(42000).WithTotalDifficulty(_blockTree.Head.TotalDifficulty + 2).TestObject;
            Block newBlock2 = Build.A.Block.WithParent(newBlock1.Header).WithReceiptsRoot(new Keccak("0xeb82c315eaf2c2a5dfc1766b075263d80e8b3ab9cb690d5304cdf114fff26939")).WithTransactions(Build.A.Transaction.SignedAndResolved(ethereumEcdsa, TestItem.PrivateKeyA, 10000000).TestObject, Build.A.Transaction.SignedAndResolved(ethereumEcdsa, TestItem.PrivateKeyB, 10000000).TestObject).WithGasUsed(42000).WithTotalDifficulty(_blockTree.Head.TotalDifficulty + 3).TestObject;
            Block newBlock3 = Build.A.Block.WithParent(newBlock2.Header).WithReceiptsRoot(new Keccak("0xeb82c315eaf2c2a5dfc1766b075263d80e8b3ab9cb690d5304cdf114fff26939")).WithTransactions(Build.A.Transaction.SignedAndResolved(ethereumEcdsa, TestItem.PrivateKeyA, 10000000).TestObject, Build.A.Transaction.SignedAndResolved(ethereumEcdsa, TestItem.PrivateKeyB, 10000000).TestObject).WithGasUsed(42000).WithTotalDifficulty(_blockTree.Head.TotalDifficulty + 4).TestObject;

            var args = new SyncModeChangedEventArgs(SyncMode.Beam, SyncMode.None);
            _blockTree.SuggestBlock(newBlock0);
            syncModeSelector.Preparing += Raise.EventWith(args);
            _blockTree.SuggestBlock(newBlock1);
            syncModeSelector.Changing += Raise.EventWith(args);
            _blockTree.SuggestBlock(newBlock2);
            syncModeSelector.Changed += Raise.EventWith(args);
            _blockTree.SuggestBlock(newBlock3);
            
            Thread.Sleep(1000);
            // _blockchainProcessor.Received().Process(newBlock0, ProcessingOptions.Beam, NullBlockTracer.Instance);
            // _blockchainProcessor.Received().Process(newBlock1, ProcessingOptions.Beam, NullBlockTracer.Instance);
            // _blockchainProcessor.Received().Process(newBlock2, ProcessingOptions.Beam, NullBlockTracer.Instance);
            // _blockchainProcessor.Received().Process(newBlock3, ProcessingOptions.Beam, NullBlockTracer.Instance);
            _blockchainProcessingQueue.DidNotReceiveWithAnyArgs().Enqueue(newBlock1, ProcessingOptions.StoreReceipts);
        }

        private void SetupBeamProcessor(ISyncModeSelector syncModeSelector = null)
        {
            MemDbProvider memDbProvider = new MemDbProvider();
            _beamBlockchainProcessor  = new BeamBlockchainProcessor(
                new ReadOnlyDbProvider(memDbProvider, false),
                _blockTree,
                MainnetSpecProvider.Instance,
                LimboLogs.Instance,
                _validator,
                NullRecoveryStep.Instance,
                new InstanceRewardCalculatorSource(NoBlockRewards.Instance),
                _blockchainProcessingQueue,
                _blockchainProcessor,
                syncModeSelector ?? new StaticSelector(SyncMode.Beam)
            );
        }

        [Test]
        public void Invalid_block_will_never_reach_actual_processor()
        {
            SetupBeamProcessor();
            Block newBlock = Build.A.Block.WithParent(_blockTree.Head).WithTotalDifficulty(_blockTree.Head.TotalDifficulty + 1).TestObject;
            newBlock.Header.Hash = Keccak.Zero;
            _blockTree.SuggestBlock(newBlock);
            _blockchainProcessor.DidNotReceiveWithAnyArgs().Process(newBlock, ProcessingOptions.None, NullBlockTracer.Instance);
        }

        [Test]
        public void Valid_block_that_would_be_skipped_will_never_reach_actual_processor()
        {
            SetupBeamProcessor();
            // setting same difficulty as head to make sure the block will be ignored
            Block newBlock = Build.A.Block.WithParent(_blockTree.Head).WithTotalDifficulty(_blockTree.Head.TotalDifficulty).TestObject;
            _blockTree.SuggestBlock(newBlock);
            _blockchainProcessor.DidNotReceiveWithAnyArgs().Process(newBlock, ProcessingOptions.None, NullBlockTracer.Instance);
        }
        
        private async Task WaitFor(Func<bool> isConditionMet, string description = "condition to be met")
        {
            const int waitInterval = 10;
            for (int i = 0; i < 100; i++)
            {
                if (isConditionMet())
                {
                    return;
                }

                TestContext.WriteLine($"({i}) Waiting {waitInterval} for {description}");
                await Task.Delay(waitInterval);
            }
        }
    }
}