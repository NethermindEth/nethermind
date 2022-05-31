//  Copyright (c) 2021 Demerzel Solutions Limited
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
// 

using System.Collections.Generic;
using System.Threading;
using FluentAssertions;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Spec;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Consensus.Comparers;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Rewards;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Db.Blooms;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.State;
using Nethermind.State.Repositories;
using Nethermind.State.Witnesses;
using Nethermind.Trie.Pruning;
using Nethermind.TxPool;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test
{
    [TestFixture]
    public class ReorgTests
    {
        private BlockchainProcessor _blockchainProcessor;
        private BlockTree _blockTree;

        [SetUp]
        public void Setup()
        {
            IDbProvider memDbProvider = TestMemDbProvider.Init();
            TrieStore trieStore = new (new MemDb(), LimboLogs.Instance);
            StateProvider stateProvider = new (trieStore, memDbProvider.CodeDb, LimboLogs.Instance);
            StorageProvider storageProvider = new (trieStore, stateProvider, LimboLogs.Instance);
            ChainLevelInfoRepository chainLevelInfoRepository = new (memDbProvider);
            ISpecProvider specProvider = MainnetSpecProvider.Instance;
            IBloomStorage bloomStorage = NullBloomStorage.Instance;
            EthereumEcdsa ecdsa = new (1, LimboLogs.Instance);
            ITransactionComparerProvider transactionComparerProvider =
                new TransactionComparerProvider(specProvider, _blockTree);
            _blockTree = new BlockTree(
                memDbProvider,
                chainLevelInfoRepository,
                specProvider,
                bloomStorage,
                new SyncConfig(),
                LimboLogs.Instance);
            TxPool.TxPool txPool = new (
                ecdsa,
                new ChainHeadInfoProvider(specProvider, _blockTree, stateProvider),
                new TxPoolConfig(),
                new TxValidator(specProvider.ChainId),
                LimboLogs.Instance, 
                transactionComparerProvider.GetDefaultComparer());
            BlockhashProvider blockhashProvider = new (_blockTree, LimboLogs.Instance);
            VirtualMachine virtualMachine = new(
                blockhashProvider,
                specProvider,
                LimboLogs.Instance);
            TransactionProcessor transactionProcessor = new (
                specProvider,
                stateProvider,
                storageProvider,
                virtualMachine,
                LimboLogs.Instance);
            
            BlockProcessor blockProcessor = new (
                MainnetSpecProvider.Instance,
                Always.Valid,
                new RewardCalculator(specProvider),
                new BlockProcessor.BlockValidationTransactionsExecutor(transactionProcessor, stateProvider),
                stateProvider,
                storageProvider,
                NullReceiptStorage.Instance,
                new WitnessCollector(memDbProvider.StateDb, LimboLogs.Instance),
                LimboLogs.Instance);
            _blockchainProcessor = new BlockchainProcessor(
                _blockTree,
                blockProcessor,
                new RecoverSignatures(
                    ecdsa,
                    txPool,
                    specProvider,
                    LimboLogs.Instance),
                LimboLogs.Instance, BlockchainProcessor.Options.Default);
        }

        [Test]
        [Retry(3)]
        public void Test()
        {
            List<Block> events = new();

            AutoResetEvent resetEvent = new AutoResetEvent(false);

            Block block0 = Build.A.Block.Genesis.WithTotalDifficulty(0L).TestObject;
            Block block1 = Build.A.Block.WithParent(block0).WithDifficulty(1).WithTotalDifficulty(1L).TestObject;
            Block block2 = Build.A.Block.WithParent(block1).WithDifficulty(2).WithTotalDifficulty(3L).TestObject;
            Block block3 = Build.A.Block.WithParent(block2).WithDifficulty(3).WithTotalDifficulty(6L).TestObject;
            Block block1B = Build.A.Block.WithParent(block0).WithDifficulty(5).WithTotalDifficulty(5L).TestObject;
            Block block2B = Build.A.Block.WithParent(block1B).WithDifficulty(6).WithTotalDifficulty(11L).TestObject;

            _blockTree.BlockAddedToMain += (_, args) =>
            {
                events.Add(args.Block);

                if (args.Block == block2B)
                {
                    resetEvent.Set();
                }
            };
            
            _blockchainProcessor.Start();
            
            _blockTree.SuggestBlock(block0);
            _blockTree.SuggestBlock(block1);
            _blockTree.SuggestBlock(block2);
            _blockTree.SuggestBlock(block3);
            _blockTree.SuggestBlock(block1B);
            _blockTree.SuggestBlock(block2B);

            resetEvent.WaitOne(2000);
            
            _blockTree.Head.Should().Be(block2B);
            events.Should().HaveCount(6);
            events[4].Hash.Should().Be(block1B.Hash);
            events[5].Hash.Should().Be(block2B.Hash);
        }
    }
}
