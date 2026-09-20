// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Autofac;
using Nethermind.Api;
using Nethermind.Blockchain;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Tracing;
using Nethermind.Core;
using Nethermind.Core.Container;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Modules;
using Nethermind.Db;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.JsonRpc.Modules.DebugModule;
using Nethermind.JsonRpc.Modules.Trace;
using Nethermind.Int256;
using Nethermind.Init.Steps;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.State;
using Nethermind.State.Flat;
using Nethermind.State.Flat.History;
using Nethermind.State.Flat.History.Changesets;
using Nethermind.State.OverridableEnv;
using NUnit.Framework;

namespace Nethermind.Runner.Test.Module;

[TestFixture]
public class TransactionChangesetIndexModuleTests
{
    [Test]
    public void GenesisBootstrap_WhenAnchorIsHistorical_LeavesImportToHistoryScan()
    {
        using MemDb code = new();
        Block anchor = Build.A.Block.WithNumber(10).TestObject;
        using BulkFillSession session = new(new MemDbFactory(), code, TestItem.KeccakA, anchor.Header, false);
        TransactionIndexGenesisBootstrap bootstrap = new(new ChainSpec(), new InitConfig { ChainSpecPath = "missing-chainspec" },
            new EthereumJsonSerializer(), LimboLogs.Instance);

        Assert.That(bootstrap.TryImport(session, CancellationToken.None), Is.False);
        Assert.That(session.IsReady, Is.False);
    }

    [TestCase(0, TestName = "GenesisBootstrap_WithCode_LeavesImportToHistoryScan")]
    [TestCase(1, TestName = "GenesisBootstrap_WithStorage_LeavesImportToHistoryScan")]
    [TestCase(2, TestName = "GenesisBootstrap_WithConstructor_LeavesImportToHistoryScan")]
    public void GenesisBootstrap_WhenAllocationsNeedExecution_LeavesImportToHistoryScan(int kind)
    {
        ChainSpecAllocation allocation = kind switch
        {
            0 => new() { Code = [0x00] },
            1 => new() { Storage = new() { [1] = [0x01] } },
            _ => new() { Constructor = [0x00] },
        };
        ChainSpec spec = new() { Allocations = new() { [TestItem.AddressA] = allocation } };
        using MemDb code = new();
        Block anchor = Build.A.Block.WithNumber(0).TestObject;
        using BulkFillSession session = new(new MemDbFactory(), code, TestItem.KeccakA, anchor.Header, false);
        TransactionIndexGenesisBootstrap bootstrap = new(spec, new InitConfig(), new EthereumJsonSerializer(), LimboLogs.Instance);

        Assert.That(bootstrap.TryImport(session, CancellationToken.None), Is.False);
        Assert.That(session.IsReady, Is.False);
    }

    [TestCase(false, TestName = "GenesisBootstrap_WithLoadedAllocations_VerifiesMainnetRoot")]
    [TestCase(true, TestName = "GenesisBootstrap_WithReleasedAllocations_ReloadsAndVerifiesMainnetRoot")]
    public void GenesisBootstrap_WhenStartingAtGenesis_VerifiesMainnetRoot(bool released)
    {
        EthereumJsonSerializer serializer = new();
        InitConfig config = new() { ChainSpecPath = "chainspec/foundation.json" };
        ChainSpec spec = new ChainSpecFileLoader(serializer, LimboLogs.Instance).LoadEmbeddedOrFromFile(config.ChainSpecPath);
        Hash256 expectedRoot = new("0xd7f8974fb5ac78d9ac099b9ad5018bedc2ce0a72dad1827a1709da30580f0544");
        Block genesis = Build.A.Block.WithNumber(0).WithStateRoot(expectedRoot).TestObject;
        if (released) spec.Allocations = null;
        using MemDb code = new();
        using BulkFillSession session = new(new MemDbFactory(), code, TestItem.KeccakA, genesis.Header, false);
        TransactionIndexGenesisBootstrap bootstrap = new(spec, config, serializer, LimboLogs.Instance);

        Assert.That(bootstrap.TryImport(session, CancellationToken.None), Is.True);
        Assert.That(session.IsReady, Is.True);
        Assert.That(session.CurrentState.StateRoot, Is.EqualTo(expectedRoot.ValueHash256));
        Assert.That(bootstrap.TryImport(session, CancellationToken.None), Is.True);
    }

    [Test]
    public void BulkReplay_WhenProcessingConsecutiveBlocks_PreservesWithdrawalsAndTransactionState()
    {
        FlatDbConfig config = new() { Enabled = true, HistoryEnabled = true, HistoryTransactionIndexEnabled = true };
        using IContainer container = new ContainerBuilder()
            .AddModule(new TestNethermindModule(config))
            .AddSingleton<ISpecProvider>(new TestSpecProvider(Cancun.Instance))
            .Build();
        using MemDb code = new();
        using SnapshotableMemColumnsDb<FlatHistoryColumns> history = new();
        HistoryRowFormat format = HistoryRowFormat.Resolve(new HistoryAvailability(history.GetColumnDb(FlatHistoryColumns.AvailableBlocks)), config);
        Block genesis = Build.A.Block.WithNumber(0).WithStateRoot(Keccak.EmptyTreeHash).TestObject;
        IBlockTree tree = container.Resolve<IBlockTree>();
        tree.SuggestBlock(genesis);
        using BulkFillSession session = new(new MemDbFactory(), code, TestItem.KeccakA, genesis.Header, false);
        foreach (FlatHistoryColumns column in new[] { FlatHistoryColumns.AccountHistory, FlatHistoryColumns.StorageHistory, FlatHistoryColumns.StorageClears })
            Assert.That(session.ImportPage((ISortedKeyValueStore)history.GetColumnDb(column), format, column, CancellationToken.None), Is.True);
        session.VerifyAnchor(CancellationToken.None);
        BulkFillScopeProvider provider = new(session, container.Resolve<ITrieNodeCache>(), container.Resolve<IResourcePool>(), config, LimboLogs.Instance);
        using ILifetimeScope scope = container.BeginLifetimeScope(builder => builder
            .AddModule(container.Resolve<IBlockValidationModule[]>())
            .AddSingleton<IWorldStateScopeProvider>(provider)
            .AddSingleton<IStateReader>(provider)
            .AddDecorator<IBlockchainProcessor, OneTimeChainProcessor>()
            .AddScoped<BlockchainProcessor.Options>(BlockchainProcessor.Options.NoReceipts));
        IBlockchainProcessor processor = scope.Resolve<IBlockchainProcessor>();
        TransactionChangesetIndex index = new(history, config);
        Withdrawal withdrawal = new() { Address = TestItem.AddressA, AmountInGwei = 1 };
        Block first = Build.A.Block.WithNumber(1).WithParent(genesis).WithPostMergeFlag(true)
            .WithBlobGasUsed(0).WithExcessBlobGas(0).WithBaseFeePerGas(0).WithWithdrawals(withdrawal).TestObject;
        Transaction transfer = Build.A.Transaction.WithTo(TestItem.AddressB).WithValue(7).WithGasPrice(0)
            .WithGasLimit(21000).WithNonce(0).SignedAndResolved(TestItem.PrivateKeyA).TestObject;
        Block second = Build.A.Block.WithNumber(2).WithParent(first).WithPostMergeFlag(true)
            .WithBlobGasUsed(0).WithExcessBlobGas(0).WithBaseFeePerGas(0).WithTransactions(transfer).WithWithdrawals().TestObject;

        foreach (Block block in new[] { first, second })
        {
            Assert.That(tree.Insert(block, BlockTreeInsertBlockOptions.SaveHeader), Is.EqualTo(AddBlockResult.Added));
            Assert.That(block.TotalDifficulty, Is.Not.Null);
            session.BeginBlock(block.Header);
            using TransactionChangesetIndex.BlockCapture capture = index.StartBlock(block.Number);
            Block isolated = block.WithReplacedHeader(block.Header.Clone());
            try
            {
                Assert.That(processor.Process(isolated, TraceProcessingOptions.ReadOnlyReplay | ProcessingOptions.ForceSequentialBlockAccessList, capture.Tracer), Is.Not.Null);
                Assert.That(capture.Commit(), Is.True);
                index.SyncWal();
                session.CommitBlock();
            }
            finally
            {
                isolated.DisposeAccountChanges();
            }
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(provider.TryGetAccount(second.Header, TestItem.AddressA, out AccountStruct sender), Is.True);
            Assert.That(sender.Balance, Is.EqualTo(withdrawal.AmountInWei - 7));
            Assert.That(sender.Nonce, Is.EqualTo(1UL));
            Assert.That(provider.TryGetAccount(second.Header, TestItem.AddressB, out AccountStruct recipient), Is.True);
            Assert.That(recipient.Balance, Is.EqualTo(new UInt256(7)));
            Assert.That(session.CurrentState.BlockNumber, Is.EqualTo(2UL));
            Assert.That(scope.Resolve<IWorldState>().IsInScope, Is.False);
        }
    }

    [TestCase(-1, 1)]
    [TestCase(1, 1)]
    [TestCase(4, 4)]
    [TestCase(64, 16)]
    public void ParallelTraceBudget_WhenConfigured_BoundsTheWorkerDegree(int configured, int expected)
    {
        using ParallelTraceBudget budget = new(new FlatDbConfig { HistoryTransactionIndexTraceParallelism = configured });
        Assert.That(budget.Degree, Is.EqualTo(expected));
    }

    [Test]
    public void ParallelTraceBudget_WhenAutomatic_UsesAvailableProcessorsUpToTheLimit()
    {
        using ParallelTraceBudget budget = new(0);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(budget.Degree, Is.InRange(1, 16));
            Assert.That(budget.Degree, Is.LessThanOrEqualTo(Environment.ProcessorCount));
            if (Environment.ProcessorCount <= 16) Assert.That(budget.Degree, Is.EqualTo(Environment.ProcessorCount));
        }
    }

    [TestCase(true, typeof(ChangesetPrefixStateSeedSource), TestName = "WithFlatHistory_TheChangesetSeedSourceWinsOverTheNullDefault")]
    [TestCase(false, typeof(NullPrefixStateSeedSource), TestName = "WithoutFlatHistory_TheNullDefaultKeepsTheReplay")]
    public void The_prefix_seed_source_follows_the_flat_history_switch(bool historyEnabled, Type expected)
    {
        using IContainer container = new ContainerBuilder()
            .AddModule(new TestNethermindModule(new FlatDbConfig { Enabled = true, HistoryEnabled = historyEnabled }))
            .Build();

        Assert.That(container.Resolve<IPrefixStateSeedSource>(), Is.TypeOf(expected));
    }

    [Test]
    public void An_executor_can_be_built_from_the_node_container()
    {
        using IContainer container = new ContainerBuilder()
            .AddModule(new TestNethermindModule(new FlatDbConfig { Enabled = true, HistoryEnabled = true, HistoryTransactionIndexEnabled = true }))
            .Build();

        using IHistoryBlockExecutor executor = container.Resolve<IHistoryBlockExecutorFactory>().Create();

        Assert.That(executor, Is.Not.Null, "the executor scope must carry everything a block replay resolves, or the builder thread dies on its first block");
    }

    [TestCase(true, TestName = "WithTheIndexOn_TheTraceEnvironmentCarriesAReadOverlaySlot")]
    [TestCase(false, TestName = "WithTheIndexOff_TheTraceEnvironmentIsUndecorated")]
    public void The_read_overlay_follows_the_index_switch(bool indexEnabled)
    {
        using IContainer container = new ContainerBuilder()
            .AddModule(new TestNethermindModule(new FlatDbConfig { Enabled = true, HistoryEnabled = true, HistoryTransactionIndexEnabled = indexEnabled }))
            .Build();

        IOverridableEnv env = container.Resolve<IOverridableEnvFactory>().Create();
        using ILifetimeScope scope = container.BeginLifetimeScope(builder => builder.AddModule(env));

        Assert.That(scope.IsRegistered<StateReadOverlaySlot>(), Is.EqualTo(indexEnabled),
            "the slot exists exactly when the scope provider consults it; a slot nothing reads would let the executor skip a prefix no one supplies");
    }

    [Test]
    public void The_debug_and_trace_module_factories_build_with_the_index_on()
    {
        using IContainer container = new ContainerBuilder()
            .AddModule(new TestNethermindModule(new FlatDbConfig { Enabled = true, HistoryEnabled = true, HistoryTransactionIndexEnabled = true }))
            .Build();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(container.Resolve<DebugModuleFactory>().Create(), Is.Not.Null, "the shared parallel tracer and its environments resolve from the node container");
            Assert.That(container.Resolve<TraceModuleFactory>().Create(), Is.Not.Null);
        }
    }

    [Test]
    public void The_builder_resolves_with_the_index_on()
    {
        using IContainer container = new ContainerBuilder()
            .AddModule(new TestNethermindModule(new FlatDbConfig { Enabled = true, HistoryEnabled = true, HistoryTransactionIndexEnabled = true, HistoryTransactionIndexWorkers = 2 }))
            .Build();

        TransactionChangesetBuilder builder = container.Resolve<TransactionChangesetBuilder>();
        builder.Dispose();

        Assert.That(container.Dispose, Throws.Nothing, "the container disposes its singletons too; a second dispose must be harmless");
    }
}
