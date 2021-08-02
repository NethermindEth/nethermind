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
// 

using System.Threading;
using FluentAssertions;
using Nethermind.Blockchain.Processing;
using Nethermind.Blockchain.Producers;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Rewards;
using Nethermind.Blockchain.Validators;
using Nethermind.Consensus;
using Nethermind.Consensus.Transactions;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Db.Blooms;
using Nethermind.Evm;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.State;
using Nethermind.State.Repositories;
using Nethermind.Trie.Pruning;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test.Producers
{
    [TestFixture]
    public class DevBlockProducerTests
    {
        [Test]
        public void Test()
        {
            ISpecProvider specProvider = MainnetSpecProvider.Instance;
            DbProvider dbProvider = new DbProvider(DbModeHint.Mem);
            dbProvider.RegisterDb(DbNames.BlockInfos, new MemDb());
            dbProvider.RegisterDb(DbNames.Blocks, new MemDb());
            dbProvider.RegisterDb(DbNames.Headers, new MemDb());
            dbProvider.RegisterDb(DbNames.State, new MemDb());
            dbProvider.RegisterDb(DbNames.Code, new MemDb());

            BlockTree blockTree = new BlockTree(
                dbProvider,
                new ChainLevelInfoRepository(dbProvider),
                specProvider,
                NullBloomStorage.Instance,
                LimboLogs.Instance);
            TrieStore trieStore = new TrieStore(
                dbProvider.RegisteredDbs[DbNames.State],
                NoPruning.Instance,
                Archive.Instance,
                LimboLogs.Instance);
            StateProvider stateProvider = new StateProvider(
                trieStore,
                dbProvider.RegisteredDbs[DbNames.Code],
                LimboLogs.Instance);
            StorageProvider storageProvider = new StorageProvider(trieStore, stateProvider, LimboLogs.Instance);
            BlockhashProvider blockhashProvider = new BlockhashProvider(blockTree, LimboLogs.Instance);
            VirtualMachine virtualMachine = new VirtualMachine(
                stateProvider,
                storageProvider,
                blockhashProvider,
                specProvider,
                LimboLogs.Instance);
            TransactionProcessor txProcessor = new TransactionProcessor(
                specProvider,
                stateProvider,
                storageProvider,
                virtualMachine,
                LimboLogs.Instance);
            BlockProcessor blockProcessor = new BlockProcessor(
                specProvider,
                Always.Valid,
                NoBlockRewards.Instance,
                new BlockProcessor.BlockValidationTransactionsExecutor(txProcessor, stateProvider),
                stateProvider,
                storageProvider,
                NullReceiptStorage.Instance,
                NullWitnessCollector.Instance,
                LimboLogs.Instance);
            BlockchainProcessor blockchainProcessor = new BlockchainProcessor(
                blockTree,
                blockProcessor,
                NullRecoveryStep.Instance,
                LimboLogs.Instance,
                BlockchainProcessor.Options.Default);
            BuildBlocksWhenRequested trigger = new BuildBlocksWhenRequested();
            var timestamper = new ManualTimestamper();
            DevBlockProducer devBlockProducer = new DevBlockProducer(
                EmptyTxSource.Instance,
                blockchainProcessor,
                stateProvider,
                blockTree,
                trigger,
                timestamper,
                specProvider,
                new MiningConfig {Enabled = true},
                LimboLogs.Instance);

            blockchainProcessor.Start();
            var suggester = new ProducedBlockSuggester(blockTree, devBlockProducer);
            devBlockProducer.Start();

            AutoResetEvent autoResetEvent = new AutoResetEvent(false);

            blockTree.NewHeadBlock += (s, e) => autoResetEvent.Set();
            blockTree.SuggestBlock(Build.A.Block.Genesis.TestObject);

            autoResetEvent.WaitOne(1000).Should().BeTrue("genesis");

            trigger.BuildBlock();
            autoResetEvent.WaitOne(1000).Should().BeTrue("1");
            blockTree.Head.Number.Should().Be(1);
        }
    }
}
