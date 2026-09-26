// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Api;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Tracing;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Tracing;
using Nethermind.Core;
using Nethermind.Core.Container;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.ServiceStopper;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Modules;
using Nethermind.Db;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules.DebugModule;
using Nethermind.JsonRpc.Modules.Trace;
using Nethermind.Int256;
using Nethermind.Init.Steps;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.State.Flat;
using Nethermind.State.Flat.History;
using Nethermind.State.Flat.History.Changesets;
using Nethermind.State.OverridableEnv;
using NUnit.Framework;
using NSubstitute;
using WorldStateSnapshot = Nethermind.Evm.State.Snapshot;

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

    [TestCase(0, TestName = "BulkReplay_AcrossBlocks_PreservesStorage")]
    [TestCase(1, TestName = "BulkReplay_AfterClear_DiscardsUnreadStorage")]
    [TestCase(2, TestName = "BulkReplay_AfterRevertedClear_PreservesStorage")]
    [TestCase(3, TestName = "BulkReplay_AfterDeleteAndRecreate_DiscardsUnreadStorage")]
    [TestCase(4, TestName = "BulkReplay_AfterNestedClearRevert_PreservesEarlierClear")]
    public void BulkReplay_WhenProcessingConsecutiveBlocks_PreservesWithdrawalsAndTransactionState(int clearMode)
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
        using ILifetimeScope scope = ProcessingTransactionIndexBulkFill.BuildReplayScope(container, provider, container.Resolve<IBlockValidationModule[]>());
        IBlockchainProcessor processor = scope.Resolve<IBlockchainProcessor>();
        Block originalHead = tree.Head;
        IReceiptStorage receipts = scope.Resolve<IReceiptStorage>();
        TransactionChangesetIndex index = new(history, config);
        Withdrawal withdrawal = new() { Address = TestItem.AddressA, AmountInGwei = 1 };
        Block first = Build.A.Block.WithNumber(1).WithParent(genesis).WithPostMergeFlag(true)
            .WithBlobGasUsed(0).WithExcessBlobGas(0).WithBaseFeePerGas(0).WithWithdrawals(withdrawal).TestObject;
        Transaction transfer = Build.A.Transaction.WithTo(TestItem.AddressB).WithValue(7).WithGasPrice(0)
            .WithGasLimit(21000).WithNonce(0).SignedAndResolved(TestItem.PrivateKeyA).TestObject;
        Block second = Build.A.Block.WithNumber(2).WithParent(first).WithPostMergeFlag(true)
            .WithBlobGasUsed(0).WithExcessBlobGas(0).WithBaseFeePerGas(0).WithTransactions(transfer).WithWithdrawals().TestObject;
        byte[] runtime = Prepare.EvmCode.PushData(0).Op(Instruction.SLOAD).PushData(1)
            .Op(Instruction.ADD).PushData(0).Op(Instruction.SSTORE)
            .PushData(0).PushData(0).PushData(0).PushData(0)
            .PushData(0).Op(Instruction.SLOAD).PushData(TestItem.AddressB).PushData(30000)
            .Op(Instruction.CALL).Op(Instruction.POP).Done;
        Transaction deployment = Build.A.Transaction.WithCode(Prepare.EvmCode.ForInitOf(runtime).Done)
            .WithValue(10).WithGasPrice(0).WithGasLimit(200000).WithNonce(1).SignedAndResolved(TestItem.PrivateKeyA).TestObject;
        Block third = NextBlock(second, deployment);
        Address contract = ContractAddress.From(TestItem.AddressA, 1);
        Transaction firstCall = Build.A.Transaction.WithTo(contract).WithValue(0).WithGasPrice(0).WithGasLimit(100000)
            .WithNonce(2).SignedAndResolved(TestItem.PrivateKeyA).TestObject;
        Block fourth = NextBlock(third, firstCall);
        Transaction secondCall = Build.A.Transaction.WithTo(contract).WithValue(0).WithGasPrice(0).WithGasLimit(100000)
            .WithNonce(3).SignedAndResolved(TestItem.PrivateKeyA).TestObject;
        Block fifth = NextBlock(fourth, secondCall);

        foreach (Block block in new[] { first, second, third, fourth, fifth })
        {
            Assert.That(tree.Insert(block, BlockTreeInsertBlockOptions.SaveHeader), Is.EqualTo(AddBlockResult.Added));
            Assert.That(block.TotalDifficulty, Is.Not.Null);
            session.BeginBlock(block.Header);
            using TransactionChangesetIndex.BlockCapture capture = index.StartBlock(block.Number);
            Block isolated = block.WithReplacedHeader(block.Header.Clone());
            try
            {
                Block processed = processor.Process(isolated, ProcessingTransactionIndexBulkFill.ReplayOptions, capture.Tracer);
                Assert.That(processed, Is.Not.Null);
                Assert.That(capture.Commit(), Is.True);
                index.SyncWal();
                session.CommitBlock();
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(tree.Head, Is.SameAs(originalHead), "bulk replay must not advance the main chain");
                    Assert.That(receipts.HasBlock(processed.Number, processed.Hash!), Is.False, "bulk replay must not persist receipts");
                }
            }
            finally
            {
                isolated.DisposeAccountChanges();
            }
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(provider.TryGetAccount(fifth.Header, TestItem.AddressA, out AccountStruct sender), Is.True);
            Assert.That(sender.Balance, Is.EqualTo(withdrawal.AmountInWei - 17));
            Assert.That(sender.Nonce, Is.EqualTo(4UL));
            Assert.That(provider.TryGetAccount(fifth.Header, TestItem.AddressB, out AccountStruct recipient), Is.True);
            Assert.That(recipient.Balance, Is.EqualTo(new UInt256(10)));
            provider.GetStorage(fifth.Header, contract, UInt256.Zero, out UInt256 stored);
            Assert.That(stored, Is.EqualTo(new UInt256(2)));
            Assert.That(session.CurrentState.BlockNumber, Is.EqualTo(5UL));
            Assert.That(scope.Resolve<IWorldState>().IsInScope, Is.False);
        }

        IWorldState state = scope.Resolve<IWorldState>();
        Block sixth = NextBlock(fifth, transfer);
        Assert.That(tree.Insert(sixth, BlockTreeInsertBlockOptions.SaveHeader), Is.EqualTo(AddBlockResult.Added));
        session.BeginBlock(sixth.Header);
        using (state.BeginScope(fifth.Header))
        {
            if (clearMode == 3)
            {
                state.DeleteAccount(contract);
                state.Commit(Cancun.Instance);
                state.CreateAccount(contract, UInt256.Zero, 1);
            }
            else if (clearMode != 0)
            {
                if (clearMode == 4)
                {
                    state.ClearStorage(contract);
                    state.Commit(Cancun.Instance);
                }
                WorldStateSnapshot snapshot = state.TakeSnapshot();
                state.ClearStorage(contract);
                if (clearMode is 2 or 4) state.Restore(snapshot);
            }
            state.Set(new StorageCell(contract, 1), 9);
            state.Commit(Cancun.Instance);
            state.CommitTree(sixth.Number);
        }
        session.CommitBlock();
        session.CleanStorage(CancellationToken.None);
        using (state.BeginScope(sixth.Header))
        {
            state.Get(new StorageCell(contract, 0), out UInt256 previous);
            state.Get(new StorageCell(contract, 1), out UInt256 added);
            Assert.That(previous, Is.EqualTo(new UInt256(clearMode is 0 or 2 ? 2u : 0u)));
            Assert.That(added, Is.EqualTo(new UInt256(9)));
        }

        Transaction wrongNonce = Build.A.Transaction.WithTo(TestItem.AddressB).WithGasPrice(0).WithGasLimit(21000)
            .WithNonce(100).SignedAndResolved(TestItem.PrivateKeyA).TestObject;
        Block invalid = NextBlock(sixth, wrongNonce);
        Assert.That(tree.Insert(invalid, BlockTreeInsertBlockOptions.SaveHeader), Is.EqualTo(AddBlockResult.Added));
        session.BeginBlock(invalid.Header);
        Assert.That(processor.Process(invalid, ProcessingTransactionIndexBulkFill.ReplayOptions, NullBlockTracer.Instance), Is.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(wrongNonce.Nonce, Is.EqualTo(100UL));
            Assert.That(session.CurrentState.BlockNumber, Is.EqualTo(sixth.Number));
            Assert.That(tree.FindBlock(invalid.Hash!, BlockTreeLookupOptions.None), Is.Not.Null,
                "a failed scratch replay must not delete a block from the shared tree");
        }
    }

    private static Block NextBlock(Block parent, Transaction transaction) => Build.A.Block
        .WithNumber(parent.Number + 1).WithParent(parent).WithPostMergeFlag(true)
        .WithBlobGasUsed(0).WithExcessBlobGas(0).WithBaseFeePerGas(0)
        .WithTransactions(transaction).WithWithdrawals().TestObject;

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

    [TestCase(true, TestName = "PrefixSeedSource_WithTheTransactionIndexOn_ArmsChangesetSeeds")]
    [TestCase(false, TestName = "PrefixSeedSource_WithTheTransactionIndexOff_ArmsNothing")]
    public void PrefixSeedSource_OnAChainWithoutAccessLists_FollowsTheFlatHistorySwitch(bool indexEnabled)
    {
        using IContainer container = new ContainerBuilder()
            .AddModule(new TestNethermindModule(new FlatDbConfig { Enabled = true, HistoryEnabled = true, HistoryTransactionIndexEnabled = indexEnabled }))
            .Build();
        Assert.That(container.Resolve<ISpecProvider>().GetFinalSpec().BlockLevelAccessListsEnabled, Is.False, "precondition: only the changeset seeds can arm a slot");

        IPrefixStateSeedSource resolved = container.Resolve<IPrefixStateSeedSource>();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(resolved, Is.TypeOf<BlockAccessListPrefixStateSeedSource>(), "every node resolves the access list decorator, the one place deciding what seeds a block");
            Assert.That(resolved.Enabled, Is.EqualTo(indexEnabled), "a block without an access list is left to the changeset source, enabled by the flat history switch");
        }
    }

    [TestCase(4, TestName = "ParallelTraceBudgets_ByDefault_TraceAccessListBlocksOnFourWorkers")]
    [TestCase(1, TestName = "ParallelTraceBudgets_WhenSetToOne_TraceAccessListBlocksSequentially")]
    [TestCase(0, TestName = "ParallelTraceBudgets_WhenSetToZero_TraceAccessListBlocksSequentially")]
    public void ParallelTraceBudgets_ForAccessListBlocks_FollowTheirOwnSetting(int configured)
    {
        JsonRpcConfig rpc = new();
        if (configured != 4) rpc.TraceBlockParallelism = configured;
        using IContainer container = new ContainerBuilder()
            .AddModule(new TestNethermindModule(new FlatDbConfig { Enabled = true, HistoryEnabled = true, HistoryTransactionIndexEnabled = false, HistoryTransactionIndexTraceParallelism = 8 }, rpc))
            .AddSingleton<ISpecProvider>(new TestSpecProvider(Amsterdam.Instance))
            .Build();
        Block block = Build.A.Block.WithNumber(1).TestObject;

        bool parallel = container.Resolve<ParallelTraceBudgets>().TryGetParallel(block.Header, out ParallelTraceBudget budget);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(new JsonRpcConfig().TraceBlockParallelism, Is.EqualTo(4), "the default is four workers");
            Assert.That(parallel, Is.EqualTo(configured >= 2 && Environment.ProcessorCount >= 2), "zero or one traces an access list block sequentially");
            if (parallel) Assert.That(budget.Degree, Is.EqualTo(Math.Min(configured, Environment.ProcessorCount)), "the degree is the access list setting, not the flat history one");
            Assert.That(container.Resolve<ParallelTraceBudgets>().AllowsParallelTracing, Is.EqualTo(parallel), "with no changeset seeds, only the access list setting can start a parallel tracer");
        }
    }

    [TestCase(true, 3, true, TestName = "ParallelTraceBudgets_WithChangesetSeeds_TraceOtherBlocksOnTheFlatHistorySetting")]
    [TestCase(true, 1, false, TestName = "ParallelTraceBudgets_WithChangesetSeedsSetToOne_TraceOtherBlocksSequentially")]
    [TestCase(false, 3, false, TestName = "ParallelTraceBudgets_WithoutChangesetSeeds_NeverTraceOtherBlocksInParallel")]
    public void ParallelTraceBudgets_ForBlocksWithoutAccessLists_FollowTheFlatHistorySetting(bool indexEnabled, int configured, bool expected)
    {
        using IContainer container = new ContainerBuilder()
            .AddModule(new TestNethermindModule(new FlatDbConfig
            {
                Enabled = true,
                HistoryEnabled = true,
                HistoryTransactionIndexEnabled = indexEnabled,
                HistoryTransactionIndexTraceParallelism = configured,
            }))
            .Build();
        Block block = Build.A.Block.WithNumber(1).TestObject;

        bool parallel = container.Resolve<ParallelTraceBudgets>().TryGetParallel(block.Header, out ParallelTraceBudget budget);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(parallel, Is.EqualTo(expected), "a block without an access list takes the changeset seeds' budget, set by the flat history setting");
            if (parallel) Assert.That(budget.Degree, Is.EqualTo(configured), "the flat history setting keeps its meaning");
            Assert.That(container.Resolve<ParallelTraceBudgets>().AllowsParallelTracing, Is.EqualTo(expected), "the parallel tracer is built only when a seed this chain can take allows two workers");
        }
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

        IOverridableEnv traceEnv = container.Resolve<ITraceEnvFactory>().CreateForTracing();
        IOverridableEnv otherEnv = container.Resolve<IOverridableEnvFactory>().Create();
        using ILifetimeScope traceScope = container.BeginLifetimeScope(builder => builder.AddModule(traceEnv));
        using ILifetimeScope otherScope = container.BeginLifetimeScope(builder => builder.AddModule(otherEnv));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(traceScope.IsRegistered<StateReadOverlaySlot>(), Is.EqualTo(indexEnabled),
                "the slot exists exactly when the scope provider consults it; a slot nothing reads would let the executor skip a prefix no one supplies");
            Assert.That(otherScope.IsRegistered<StateReadOverlaySlot>(), Is.False, "only trace environments carry the overlay; every other read-only environment pays nothing for it");
        }
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

    // The builder stops before the databases it writes to are disposed, and it is the middleware that arranges that
    // for every IStoppableService it activates - so resolving it is what has to register it, exactly once.
    [Test]
    public async Task TransactionChangesetBuilder_WhenResolvedAndStarted_IsRegisteredOnceForPreDisposalStop()
    {
        IServiceStopper stopper = Substitute.For<IServiceStopper>();
        using IContainer container = new ContainerBuilder()
            .AddModule(new TestNethermindModule(new FlatDbConfig { Enabled = true, HistoryEnabled = true }))
            .AddSingleton<IServiceStopper>(stopper)
            .Build();

        TransactionChangesetBuilder builder = container.Resolve<TransactionChangesetBuilder>();
        await container.Resolve<StartTransactionChangesetBuilder>().Execute(CancellationToken.None);

        stopper.Received(1).AddStoppable(builder);
    }
}
