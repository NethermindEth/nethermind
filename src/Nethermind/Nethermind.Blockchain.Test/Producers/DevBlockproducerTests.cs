// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using FluentAssertions;
using Nethermind.Blockchain.BeaconBlockRoot;
using Nethermind.Blockchain.Blocks;
using Nethermind.Blockchain.Receipts;
using Nethermind.Config;
using Nethermind.Consensus;
using Nethermind.Consensus.ExecutionRequests;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.Rewards;
using Nethermind.Consensus.Transactions;
using Nethermind.Consensus.Validators;
using Nethermind.Consensus.Withdrawals;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Evm;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.State;
using Nethermind.Trie.Pruning;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test.Producers;

public class DevBlockProducerTests
{
    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Test()
    {
        ISpecProvider specProvider = MainnetSpecProvider.Instance;
        DbProvider dbProvider = new();
        dbProvider.RegisterDb(DbNames.BlockInfos, new MemDb());
        dbProvider.RegisterDb(DbNames.Blocks, new MemDb());
        dbProvider.RegisterDb(DbNames.Headers, new MemDb());
        dbProvider.RegisterDb(DbNames.State, new MemDb());
        dbProvider.RegisterDb(DbNames.Code, new MemDb());
        dbProvider.RegisterDb(DbNames.Metadata, new MemDb());

        BlockTree blockTree = Build.A.BlockTree()
            .WithoutSettingHead
            .TestObject;

        TrieStore trieStore = TestTrieStoreFactory.Build(dbProvider.RegisteredDbs[DbNames.State],
            NoPruning.Instance,
            Archive.Instance,
            LimboLogs.Instance);
        WorldState stateProvider = new(
            trieStore,
            dbProvider.RegisteredDbs[DbNames.Code],
            LimboLogs.Instance);
        StateReader stateReader = new(trieStore, dbProvider.GetDb<IDb>(DbNames.State), LimboLogs.Instance);
        BlockhashProvider blockhashProvider = new(blockTree, specProvider, stateProvider, LimboLogs.Instance);
        CodeInfoRepository codeInfoRepository = new();
        VirtualMachine virtualMachine = new(
            blockhashProvider,
            specProvider,
            LimboLogs.Instance);
        TransactionProcessor txProcessor = new(
            specProvider,
            stateProvider,
            virtualMachine,
            codeInfoRepository,
            LimboLogs.Instance);
        BlockProcessor blockProcessor = new BlockProcessor(
            specProvider,
            Always.Valid,
            NoBlockRewards.Instance,
            new BlockProcessor.BlockValidationTransactionsExecutor(txProcessor, stateProvider),
            stateProvider,
            NullReceiptStorage.Instance,
            new BeaconBlockRootHandler(txProcessor, stateProvider),
            new BlockhashStore(specProvider, stateProvider),
            LimboLogs.Instance,
            new WithdrawalProcessor(stateProvider, LimboLogs.Instance),
            new ExecutionRequestsProcessor(txProcessor));
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
            timestamper,
            specProvider,
            new BlocksConfig(),
            LimboLogs.Instance);

        StandardBlockProducerRunner blockProducerRunner = new StandardBlockProducerRunner(trigger, blockTree, devBlockProducer);

        blockchainProcessor.Start();
        blockProducerRunner.Start();
        ProducedBlockSuggester _ = new ProducedBlockSuggester(blockTree, blockProducerRunner);

        AutoResetEvent autoResetEvent = new(false);

        blockTree.NewHeadBlock += (_, _) => autoResetEvent.Set();
        blockTree.SuggestBlock(Build.A.Block.Genesis.TestObject);

        autoResetEvent.WaitOne(1000).Should().BeTrue("genesis");

        trigger.BuildBlock();
        autoResetEvent.WaitOne(1000).Should().BeTrue("1");
        blockTree.Head!.Number.Should().Be(1);
    }
}
