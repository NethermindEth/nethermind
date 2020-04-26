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
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.Synchronization.BeamSync;
using Nethermind.Synchronization.ParallelSync;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Synchronization.Test.BeamSync
{
    [Parallelizable(ParallelScope.Self)]
    [TestFixture]
    public class BeamBlockchainProcessorTests
    {
        private BlockTree _blockTree;
        private BlockValidator _validator;
        private IBlockProcessingQueue _blockchainProcessor;

        [SetUp]
        public void SetUp()
        {
            _blockchainProcessor = Substitute.For<IBlockProcessingQueue>();
            _blockTree = Build.A.BlockTree().OfChainLength(10).TestObject;
            HeaderValidator headerValidator = new HeaderValidator(_blockTree, NullSealEngine.Instance, MainnetSpecProvider.Instance, LimboLogs.Instance);
            _validator = new BlockValidator(Always.Valid, headerValidator, Always.Valid, MainnetSpecProvider.Instance, LimboLogs.Instance);
        }

        [Test, Retry(3)]
        public void Valid_block_makes_it_all_the_way()
        {
            SetupBeamProcessor(SyncMode.Beam);
            Block newBlock = Build.A.Block.WithParent(_blockTree.Head).WithTotalDifficulty(_blockTree.Head.TotalDifficulty + 1).TestObject;
            _blockTree.SuggestBlock(newBlock);
            Thread.Sleep(1000);
            _blockchainProcessor.Received().Enqueue(newBlock, ProcessingOptions.Beam);
        }

        [Test, Retry(3)]
        public void Valid_block_with_transactions_makes_it_all_the_way()
        {
            SetupBeamProcessor(SyncMode.Beam);
            EthereumEcdsa ethereumEcdsa = new EthereumEcdsa(MainnetSpecProvider.Instance, LimboLogs.Instance);
            Block newBlock = Build.A.Block.WithParent(_blockTree.Head).WithReceiptsRoot(new Keccak("0xeb82c315eaf2c2a5dfc1766b075263d80e8b3ab9cb690d5304cdf114fff26939")).WithTransactions(Build.A.Transaction.SignedAndResolved(ethereumEcdsa, TestItem.PrivateKeyA, 10000000).TestObject, Build.A.Transaction.SignedAndResolved(ethereumEcdsa, TestItem.PrivateKeyB, 10000000).TestObject).WithGasUsed(42000).WithTotalDifficulty(_blockTree.Head.TotalDifficulty + 1).TestObject;
            _blockTree.SuggestBlock(newBlock);
            Thread.Sleep(1000);
            _blockchainProcessor.Received().Enqueue(newBlock, ProcessingOptions.Beam);
        }
        
        [Test, Retry(3)]
        public void Valid_block_with_transactions_makes_it_is_processed_normally_if_beam_syncing_finished()
        {
            SetupBeamProcessor(SyncMode.None);
            EthereumEcdsa ethereumEcdsa = new EthereumEcdsa(MainnetSpecProvider.Instance, LimboLogs.Instance);
            Block newBlock = Build.A.Block.WithParent(_blockTree.Head).WithReceiptsRoot(new Keccak("0xeb82c315eaf2c2a5dfc1766b075263d80e8b3ab9cb690d5304cdf114fff26939")).WithTransactions(Build.A.Transaction.SignedAndResolved(ethereumEcdsa, TestItem.PrivateKeyA, 10000000).TestObject, Build.A.Transaction.SignedAndResolved(ethereumEcdsa, TestItem.PrivateKeyB, 10000000).TestObject).WithGasUsed(42000).WithTotalDifficulty(_blockTree.Head.TotalDifficulty + 1).TestObject;
            _blockTree.SuggestBlock(newBlock);
            Thread.Sleep(1000);
            _blockchainProcessor.Received().Enqueue(newBlock, ProcessingOptions.StoreReceipts);
        }

        private void SetupBeamProcessor(SyncMode syncMode = SyncMode.Beam)
        {
            MemDbProvider memDbProvider = new MemDbProvider();
            _ = new BeamBlockchainProcessor(
                new ReadOnlyDbProvider(memDbProvider, false),
                _blockTree,
                MainnetSpecProvider.Instance,
                LimboLogs.Instance,
                _validator,
                NullRecoveryStep.Instance,
                new InstanceRewardCalculatorSource(NoBlockRewards.Instance),
                _blockchainProcessor,
                new StaticSelector(SyncMode.Beam)
            );
        }

        [Test]
        public void Invalid_block_will_never_reach_actual_processor()
        {
            SetupBeamProcessor(SyncMode.Beam);
            Block newBlock = Build.A.Block.WithParent(_blockTree.Head).WithTotalDifficulty(_blockTree.Head.TotalDifficulty + 1).TestObject;
            newBlock.Header.Hash = Keccak.Zero;
            _blockTree.SuggestBlock(newBlock);
            _blockchainProcessor.DidNotReceiveWithAnyArgs().Enqueue(newBlock, ProcessingOptions.None);
        }

        [Test]
        public void Valid_block_that_would_be_skipped_will_never_reach_actual_processor()
        {
            SetupBeamProcessor(SyncMode.Beam);
            // setting same difficulty as head to make sure the block will be ignored
            Block newBlock = Build.A.Block.WithParent(_blockTree.Head).WithTotalDifficulty(_blockTree.Head.TotalDifficulty).TestObject;
            _blockTree.SuggestBlock(newBlock);
            _blockchainProcessor.DidNotReceiveWithAnyArgs().Enqueue(newBlock, ProcessingOptions.None);
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