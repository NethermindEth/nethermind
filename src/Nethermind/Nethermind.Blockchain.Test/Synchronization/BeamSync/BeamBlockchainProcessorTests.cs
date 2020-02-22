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

using System.Threading;
using Nethermind.Blockchain.Rewards;
using Nethermind.Blockchain.Synchronization.BeamSync;
using Nethermind.Blockchain.Test.Validators;
using Nethermind.Blockchain.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Mining;
using Nethermind.Specs;
using Nethermind.Store;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test.Synchronization.BeamSync
{
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
            HeaderValidator headerValidator = new HeaderValidator(_blockTree, NullSealEngine.Instance, MainNetSpecProvider.Instance, LimboLogs.Instance);
            _validator = new BlockValidator(TestTxValidator.AlwaysValid, headerValidator, AlwaysValidOmmersValidator.Instance, MainNetSpecProvider.Instance, LimboLogs.Instance);
            SetupBeamProcessor();
        }

        [Test, Retry(3)]
        public void Valid_block_makes_it_all_the_way()
        {
            Block newBlock = Build.A.Block.WithParent(_blockTree.Head).WithTotalDifficulty(_blockTree.Head.TotalDifficulty + 1).TestObject;
            _blockTree.SuggestBlock(newBlock);
            Thread.Sleep(1000);
            _blockchainProcessor.Received().Enqueue(newBlock, ProcessingOptions.IgnoreParentNotOnMainChain);
        }

        [Test, Retry(3)]
        public void Valid_block_with_transactions_makes_it_all_the_way()
        {
            EthereumEcdsa ethereumEcdsa = new EthereumEcdsa(MainNetSpecProvider.Instance, LimboLogs.Instance);
            Block newBlock = Build.A.Block.WithParent(_blockTree.Head).WithReceiptsRoot(new Keccak("0xeb82c315eaf2c2a5dfc1766b075263d80e8b3ab9cb690d5304cdf114fff26939")).WithTransactions(Build.A.Transaction.SignedAndResolved(ethereumEcdsa, TestItem.PrivateKeyA, 10000000).TestObject, Build.A.Transaction.SignedAndResolved(ethereumEcdsa, TestItem.PrivateKeyB, 10000000).TestObject).WithGasUsed(42000).WithTotalDifficulty(_blockTree.Head.TotalDifficulty + 1).TestObject;
            _blockTree.SuggestBlock(newBlock);
            Thread.Sleep(1000);
            _blockchainProcessor.Received().Enqueue(newBlock, ProcessingOptions.IgnoreParentNotOnMainChain);
        }

        private void SetupBeamProcessor()
        {
            MemDbProvider memDbProvider = new MemDbProvider();
            _ = new BeamBlockchainProcessor(
                new ReadOnlyDbProvider(memDbProvider, false),
                _blockTree,
                MainNetSpecProvider.Instance,
                LimboLogs.Instance,
                _validator,
                NullRecoveryStep.Instance,
                new InstanceRewardCalculatorSource(NoBlockRewards.Instance),
                _blockchainProcessor
            );
        }

        [Test]
        public void Invalid_block_will_never_reach_actual_processor()
        {
            Block newBlock = Build.A.Block.WithParent(_blockTree.Head).WithTotalDifficulty(_blockTree.Head.TotalDifficulty + 1).TestObject;
            newBlock.Header.Hash = Keccak.Zero;
            _blockTree.SuggestBlock(newBlock);
            _blockchainProcessor.DidNotReceiveWithAnyArgs().Enqueue(newBlock, ProcessingOptions.None);
        }

        [Test]
        public void Valid_block_that_would_be_skipped_will_never_reach_actual_processor()
        {
            // setting same difficulty as head to make sure the block will be ignored
            Block newBlock = Build.A.Block.WithParent(_blockTree.Head).WithTotalDifficulty(_blockTree.Head.TotalDifficulty).TestObject;
            _blockTree.SuggestBlock(newBlock);
            _blockchainProcessor.DidNotReceiveWithAnyArgs().Enqueue(newBlock, ProcessingOptions.None);
        }
    }
}