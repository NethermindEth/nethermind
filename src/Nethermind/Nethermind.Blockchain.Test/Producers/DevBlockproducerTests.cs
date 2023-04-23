// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using FluentAssertions;
using Nethermind.Blockchain.Receipts;
using Nethermind.Config;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.Rewards;
using Nethermind.Consensus.Transactions;
using Nethermind.Consensus.Validators;
using Nethermind.Consensus.Withdrawals;
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
        [Test, Timeout(Timeout.MaxTestTime)]
        public void Test()
        {
            ISpecProvider specProvider = MainnetSpecProvider.Instance;
            DbProvider dbProvider = new(DbModeHint.Mem);
            dbProvider.RegisterDb(DbNames.BlockInfos, new MemDb());
            dbProvider.RegisterDb(DbNames.Blocks, new MemDb());
            dbProvider.RegisterDb(DbNames.Headers, new MemDb());
            dbProvider.RegisterDb(DbNames.State, new MemDb());
            dbProvider.RegisterDb(DbNames.Code, new MemDb());
            dbProvider.RegisterDb(DbNames.Metadata, new MemDb());

            BlockTree blockTree = new(
                dbProvider,
                new ChainLevelInfoRepository(dbProvider),
                specProvider,
                NullBloomStorage.Instance,
                LimboLogs.Instance);
            TrieStoreByPath trieStore = new(
                dbProvider.RegisteredDbs[DbNames.State],
                NoPruning.Instance,
                Archive.Instance,
                LimboLogs.Instance);
            StateProvider stateProvider = new(
                trieStore,
                dbProvider.RegisteredDbs[DbNames.Code],
                LimboLogs.Instance);
            StateReader stateReader = new(trieStore, dbProvider.GetDb<IDb>(DbNames.State), LimboLogs.Instance);
            StorageProvider storageProvider = new(trieStore, stateProvider, LimboLogs.Instance);
            BlockhashProvider blockhashProvider = new(blockTree, LimboLogs.Instance);
            VirtualMachine virtualMachine = new(
                blockhashProvider,
                specProvider,
                LimboLogs.Instance);
            TransactionProcessor txProcessor = new(
                specProvider,
                stateProvider,
                storageProvider,
                virtualMachine,
                LimboLogs.Instance);
            BlockProcessor blockProcessor = new(
                specProvider,
                Always.Valid,
                NoBlockRewards.Instance,
                new BlockProcessor.BlockValidationTransactionsExecutor(txProcessor, stateProvider),
                stateProvider,
                storageProvider,
                NullReceiptStorage.Instance,
                NullWitnessCollector.Instance,
                LimboLogs.Instance);
            BlockchainProcessor blockchainProcessor = new(
                blockTree,
                blockProcessor,
                NullRecoveryStep.Instance,
                stateReader,
                LimboLogs.Instance,
                BlockchainProcessor.Options.Default);
            BuildBlocksWhenRequested trigger = new();
            ManualTimestamper timestamper = new ManualTimestamper();
            DevBlockProducer devBlockProducer = new(
                EmptyTxSource.Instance,
                blockchainProcessor,
                stateProvider,
                blockTree,
                trigger,
                timestamper,
                specProvider,
                new BlocksConfig(),
                LimboLogs.Instance);

            blockchainProcessor.Start();
            devBlockProducer.Start();
            ProducedBlockSuggester suggester = new ProducedBlockSuggester(blockTree, devBlockProducer);

            AutoResetEvent autoResetEvent = new(false);

            blockTree.NewHeadBlock += (s, e) => autoResetEvent.Set();
            blockTree.SuggestBlock(Build.A.Block.Genesis.TestObject);

            autoResetEvent.WaitOne(1000).Should().BeTrue("genesis");

            trigger.BuildBlock();
            autoResetEvent.WaitOne(1000).Should().BeTrue("1");
            blockTree.Head.Number.Should().Be(1);
        }
    }
}
