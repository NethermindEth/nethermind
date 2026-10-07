// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Autofac;
using Nethermind.Core.Memory;
using Nethermind.Core.Test.Modules;
using Nethermind.State.Pbt.Migration;
using Nethermind.State.Pbt.Mirror;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Threading;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.JsonRpc.Modules;
using NUnit.Framework;
using NSubstitute;
using Nethermind.Logging;
using Nethermind.Pbt;
using Nethermind.Monitoring.Config;
using Nethermind.State.Pbt.Persistence;
using Nethermind.State.Pbt.Steps;
using Nethermind.Api.Steps;
using Nethermind.Init.Steps;

namespace Nethermind.State.Pbt.Test;

public class PbtDbManagerTests
{
    [Test]
    public async Task Production_modules_share_trie_cache_and_report_inactive_migration([Values] bool mirror)
    {
        PbtConfig config = new() { Enabled = !mirror, MirrorFlat = mirror };
        await using IContainer container = BuildProductionContainer(config);
        PbtTrieNodeCache cache = container.Resolve<PbtTrieNodeCache>();
        using ILifetimeScope child = container.BeginLifetimeScope();
        IMigrationDebugRpcModule rpcModule = container.Resolve<IMigrationDebugRpcModule>();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(child.Resolve<PbtTrieNodeCache>(), Is.SameAs(cache));
            Assert.That(container.Resolve<IPbtDbManager>(), Is.TypeOf<PbtDbManager>());
            Assert.That(config.AccountTrieNodeCacheSizeBudget, Is.EqualTo(134217728UL));
            Assert.That(config.CodeTrieNodeCacheSizeBudget, Is.EqualTo(33554432UL));
            Assert.That(config.StorageTrieNodeCacheSizeBudget, Is.EqualTo(234881024UL));
            Assert.That(container.Resolve<IReadOnlyList<RpcModuleInfo>>().Select(static m => m.ModuleType), Has.Member(typeof(IMigrationDebugRpcModule)));
            Assert.That(rpcModule.debug_migrationProgress().Data, Is.EqualTo(MigrationProgressForRpc.Inactive));
            Assert.That(rpcModule.debug_shadowStateRoot(TestItem.KeccakA).Data, Is.Null);
        }
    }

    [Test]
    public async Task Command_steps_are_registered_and_selected_by_config([Values] bool mirror, [Values] bool import, [Values] bool scan)
    {
        PbtConfig config = new() { Enabled = !mirror, MirrorFlat = mirror, ImportFromPreimageFlat = import, ScanTree = scan };
        await using IContainer container = BuildProductionContainer(config);

        List<Type> expectedTargets = [];
        if (import) expectedTargets.Add(typeof(ImportPbtFromPreimageFlat));
        if (scan && !mirror) expectedTargets.Add(typeof(ScanPbtTree));
        StepInfo[] steps = [.. container.Resolve<IEnumerable<StepInfo>>()];
        IEnumerable<string?> commands = steps.Select(static step => step.Command);
        using (Assert.EnterMultipleScope())
        {
            // A command run is pruned to its dependency closure, so metrics only start if the command asks for them.
            Assert.That(steps.Where(static step => step.Command is "import-pbt" or "scan-pbt").Select(static step => step.Dependencies),
                Has.All.Contains(typeof(StartMonitoring)));
            Assert.That(commands, Does.Contain("import-pbt"));
            Assert.That(commands.Contains("scan-pbt"), Is.EqualTo(!mirror));
            Assert.That(container.Resolve<IEnumerable<StepTarget>>().Select(static target => target.StepBaseType), Is.EquivalentTo(expectedTargets));
        }
    }

    private static IContainer BuildProductionContainer(PbtConfig config) => new ContainerBuilder()
        .AddModule(new TestNethermindModule(config))
        .AddModule(config.MirrorFlat ? new PbtMirrorModule(config) : new PbtModule(config))
        .Build();

    private static readonly Address Address = TestItem.AddressA;

    /// <summary>The per-block storage slot, on a stem separate from the account header.</summary>
    private static readonly UInt256 Slot = 1000;

    private static Hash256 CommitBlock(IWorldStateScopeProvider.IScope scope, ulong blockNumber, in UInt256 balance)
    {
        using (IWorldStateScopeProvider.IWorldStateWriteBatch batch = scope.StartWriteBatch(1))
        {
            batch.Set(Address, new Account(blockNumber, balance));
            using IWorldStateScopeProvider.IStorageWriteBatch storage = batch.CreateStorageWriteBatch(Address, 1);
            storage.Set(Slot, (UInt256)blockNumber);
        }

        scope.UpdateRootHash();
        scope.Commit(blockNumber);
        return scope.RootHash;
    }

    private static BlockHeader Header(ulong number, Hash256 root) => Build.A.BlockHeader.WithNumber(number).WithStateRoot(root).TestObject;

    [Test]
    public async Task ReadOnlyBundle_IsSharedPerState_UntilTheBoundarySweepReleasesIt()
    {
        await using PbtTestContext ctx = new();
        Hash256 root1;
        using (IWorldStateScopeProvider.IScope scope = ctx.CreateScopeProvider().BeginScope(null, new LocalMetrics()))
        {
            root1 = CommitBlock(scope, 1, 100);
        }

        StateId state = new(1, root1);
        IPbtDbManager manager = ctx.Manager;
        PbtReadOnlySnapshotBundle first = manager.GatherReadOnlyBundle(state);
        PbtReadOnlySnapshotBundle second = manager.GatherReadOnlyBundle(state);
        Assert.That(second, Is.SameAs(first), "one state, one shared view");

        // The gather retains its leased view after the sweep drops the cached view.
        ctx.Manager.FlushCache(default);
        Assert.That(first.GetAccount(Address)!.Value.ToAccount().Balance, Is.EqualTo((UInt256)100));

        PbtReadOnlySnapshotBundle afterSweep = manager.GatherReadOnlyBundle(state);
        Assert.That(afterSweep, Is.Not.SameAs(first), "the swept view is not handed out again");

        first.Dispose();
        second.Dispose();
        afterSweep.Dispose();
    }

    [Test]
    public async Task CommitFlushReopen_ServesPersistedState_AndPrunesHistory()
    {
        SnapshotableMemColumnsDb<PbtColumns> db = new("pbt");
        Hash256 root1;
        Hash256 root3;

        await using (PbtTestContext ctx = new(db))
        {
            using (IWorldStateScopeProvider.IScope scope = ctx.CreateScopeProvider().BeginScope(null, new LocalMetrics()))
            {
                root1 = CommitBlock(scope, 1, 100);
                CommitBlock(scope, 2, 200);
                root3 = CommitBlock(scope, 3, 300);
            }

            ctx.Manager.FlushCache(default);
            using Persistence.IPbtPersistence.IReader reader = ctx.Persistence.CreateReader();
            Assert.That(reader.CurrentState, Is.EqualTo(new StateId(3, root3)));
        }

        await using (PbtTestContext reopened = new(db))
        {
            Assert.That(reopened.Manager.HasStateForBlock(new StateId(3, root3)), Is.True);
            Assert.That(reopened.Manager.HasStateForBlock(new StateId(1, root1)), Is.False);
            Assert.That(reopened.Manager.TryGatherReadOnlyBundle(new StateId(1, root1)), Is.Null);

            using IWorldStateScopeProvider.IScope scope = reopened.CreateScopeProvider().BeginScope(Header(3, root3), new LocalMetrics());
            Account? account = scope.Get(Address);
            Assert.That(account, Is.Not.Null);
            Assert.That(account!.Nonce, Is.EqualTo(3ul));
            Assert.That(account.Balance, Is.EqualTo((UInt256)300));
            Assert.That(scope.CreateStorageTree(Address).Get(Slot), Is.EqualTo((UInt256)3), "and the slot decodes out of its own persisted blob");
        }
    }

    [Test]
    public async Task ForkCommitsFromSameParent_BothStatesReadable()
    {
        await using PbtTestContext ctx = new();
        Hash256 root1;
        using (IWorldStateScopeProvider.IScope scope = ctx.CreateScopeProvider().BeginScope(null, new LocalMetrics()))
        {
            root1 = CommitBlock(scope, 1, 100);
        }

        Hash256 rootA;
        Hash256 rootB;
        using (IWorldStateScopeProvider.IScope scopeA = ctx.CreateScopeProvider().BeginScope(Header(1, root1), new LocalMetrics()))
        {
            rootA = CommitBlock(scopeA, 2, 222);
        }

        using (IWorldStateScopeProvider.IScope scopeB = ctx.CreateScopeProvider().BeginScope(Header(1, root1), new LocalMetrics()))
        {
            rootB = CommitBlock(scopeB, 2, 333);
        }

        Assert.That(rootA, Is.Not.EqualTo(rootB));
        Assert.That(ctx.Manager.HasStateForBlock(new StateId(2, rootA)), Is.True);
        Assert.That(ctx.Manager.HasStateForBlock(new StateId(2, rootB)), Is.True);

        using (IWorldStateScopeProvider.IScope onA = ctx.CreateScopeProvider(isReadOnly: true).BeginScope(Header(2, rootA), new LocalMetrics()))
        {
            Assert.That(onA.Get(Address)!.Balance, Is.EqualTo((UInt256)222));
        }

        using (IWorldStateScopeProvider.IScope onB = ctx.CreateScopeProvider(isReadOnly: true).BeginScope(Header(2, rootB), new LocalMetrics()))
        {
            Assert.That(onB.Get(Address)!.Balance, Is.EqualTo((UInt256)333));
        }

        using IWorldStateScopeProvider.IScope onParent = ctx.CreateScopeProvider(isReadOnly: true).BeginScope(Header(1, root1), new LocalMetrics());
        Assert.That(onParent.Get(Address)!.Balance, Is.EqualTo((UInt256)100));
    }

    [Test]
    public async Task FinalizedTrigger_PersistsCanonicalSegments_AndPrunesRepository()
    {
        await using PbtTestContext ctx = new(config: new PbtConfig { CompactSize = 2, MinReorgDepth = 1, MaxReorgDepth = 100 });

        Hash256[] roots = new Hash256[6];
        using IWorldStateScopeProvider.IScope scope = ctx.CreateScopeProvider().BeginScope(null, new LocalMetrics());
        for (ulong number = 1; number <= 5; number++)
        {
            roots[number] = CommitBlock(scope, number, number * 100);
            ctx.FinalizedStateProvider.SetCanonicalRoot(number, roots[number]);
        }

        ctx.FinalizedStateProvider.FinalizedBlockNumber = 5;
        ctx.Coordinator.CheckPersistence(ctx.Repository.GetLastCommittedStateId()!.Value);

        // With CompactSize 2 and no offset, only even finalized blocks are persisted.
        Assert.That(ctx.Coordinator.GetCurrentPersistedStateId(), Is.EqualTo(new StateId(4, roots[4])));
        Assert.That(ctx.Repository.Count, Is.EqualTo(1));

        // The open scope continues reading through its leased layers.
        Assert.That(scope.Get(Address)!.Balance, Is.EqualTo((UInt256)500));
    }

    /// <summary>
    /// The block's header already claims a root, so that is what the scope must report and key its
    /// state by; the root the tree folds to is kept beside it, on the sealed layer, for the next fold.
    /// Committing must also carry the resolved header forward, or the block after it in the same
    /// branch would resolve the child of the block just committed — itself. Persisted state is restored
    /// by the header root while continuing from the persisted tree root.
    /// </summary>
    [Test]
    public async Task PersistedState_IsKeyedByTheHeaderRoot_WithTheTreeRootRecordedBesideIt()
    {
        SnapshotableMemColumnsDb<PbtColumns> db = new("pbt");
        PbtTestChildHeaders childHeaders = new();
        BlockHeader second;
        ValueHash256 treeRoot;

        await using (PbtTestContext ctx = new(db, childHeaders: childHeaders))
        {
            // Block 0 has no header root, so genesis claims its tree root.
            BlockHeader genesis;
            using (IWorldStateScopeProvider.IScope genesisScope = ctx.CreateScopeProvider().BeginScope(null, new LocalMetrics()))
            {
                genesis = Header(0, CommitBlock(genesisScope, 0, 1));
            }

            BlockHeader first = childHeaders.Add(genesis, TestItem.KeccakA);
            second = childHeaders.Add(first, TestItem.KeccakB);
            using (IWorldStateScopeProvider.IScope scope = ctx.CreateScopeProvider().BeginScope(genesis, new LocalMetrics()))
            {
                Assert.That(CommitBlock(scope, 1, 100), Is.EqualTo(TestItem.KeccakA), "the block reports the root its header claims");
                Assert.That(CommitBlock(scope, 2, 200), Is.EqualTo(TestItem.KeccakB), "and the next block in the branch resolves its own header");
            }

            using (Assert.EnterMultipleScope())
            {
                Assert.That(ctx.Manager.HasStateForBlock(new StateId(first)), Is.True, "both states are keyed by their header");
                Assert.That(ctx.Manager.HasStateForBlock(new StateId(second)), Is.True);
            }

            using (PbtSnapshotBundle bundle = ((IPbtDbManager)ctx.Manager).GatherBundle(new StateId(second), PbtResourcePool.Usage.ReadOnlyProcessingEnv))
            {
                treeRoot = bundle.TreeRoot;
                Assert.That(bundle.GetAccount(Address)!.Balance, Is.EqualTo((UInt256)200), "the state is readable through the header-keyed id");
            }

            Assert.That(treeRoot, Is.Not.EqualTo(TestItem.KeccakB.ValueHash256), "which is not the root the tree folded to");
            ctx.Manager.FlushCache(default);
        }

        await using (PbtTestContext reopened = new(db, childHeaders: childHeaders))
        {
            using (Persistence.IPbtPersistence.IReader reader = reopened.Persistence.CreateReader())
            {
                Assert.That(reader.CurrentState, Is.EqualTo(new StateId(second)));
                Assert.That(reader.CurrentRoot, Is.EqualTo(treeRoot));
            }

            BlockHeader third = childHeaders.Add(second, TestItem.KeccakC);
            using IWorldStateScopeProvider.IScope scope = reopened.CreateScopeProvider().BeginScope(second, new LocalMetrics());
            Assert.That(scope.Get(Address)!.Balance, Is.EqualTo((UInt256)200), "the persisted state is found by its header");
            Assert.That(CommitBlock(scope, 3, 300), Is.EqualTo(third.StateRoot), "and the branch carries on from it");
        }
    }

    [TestCase(32, 0)]
    [TestCase(1, 0)]
    [TestCase(32, 1)]
    [TestCase(1, 1)]
    [TestCase(32, 2)]
    [TestCase(1, 2)]
    [TestCase(32, 3)]
    [TestCase(1, 3)]
    public void Persistence_PrefersExistingUnits_AndBoundsBackgroundDrain(int width, int mode)
    {
        using CoordinatorHarness harness = new(new PbtConfig { CompactSize = 32, CompactionOffset = 0, MinReorgDepth = 0, MaxReorgDepth = 32, MirrorFlat = mode == 2 });
        List<(StateId From, StateId To)> writes = [];
        harness.Persistence.CreateWriteBatch(Arg.Any<StateId>(), Arg.Any<StateId>(), Arg.Any<ValueHash256>(), Arg.Any<WriteFlags>())
            .Returns(call =>
            {
                StateId from = call.ArgAt<StateId>(0);
                StateId to = call.ArgAt<StateId>(1);
                Assert.That(call.ArgAt<ValueHash256>(2), Is.EqualTo(to.StateRoot));
                writes.Add((from, to));
                return Substitute.For<IPbtPersistence.IWriteBatch>();
            });
        for (int number = 1; number <= 192; number++)
        {
            harness.Repository.TryAdd(PersistenceSnapshot(number - 1, number, harness.Pool));
            harness.Finalized.SetCanonicalRoot((ulong)number, TestItem.KeccakA);
            if (width > 1 && number % width == 0)
                harness.Repository.TryAddCompacted(PersistenceSnapshot(number - width, number, harness.Pool));
        }
        if (mode == 3) harness.Finalized.FinalizedBlockNumber = 192;
        if (mode is 0 or 3) harness.Coordinator.CheckPersistence(PersistenceState(192));
        else if (mode == 1) harness.Coordinator.FlushToPersistence();
        else Assert.That(harness.Coordinator.PersistUpTo(PersistenceState(192)), Is.True);

        Assert.That(writes.Count, Is.EqualTo(mode is 0 or 3 ? 4 : 192 / width));
        for (int index = 0; index < writes.Count; index++)
            Assert.That(writes[index], Is.EqualTo((PersistenceState(index * width), PersistenceState((index + 1) * width))));
        Assert.That(harness.Coordinator.GetCurrentPersistedStateId(), Is.EqualTo(writes[^1].To));
    }

    [Test]
    public void Persistence_FailedCommitDoesNotPublishOrPrune_AndUnknownMirrorSeedDoesNotAdvance()
    {
        using CoordinatorHarness harness = new(new PbtConfig { CompactSize = 2, CompactionOffset = 0, MirrorFlat = true });
        bool failCommit = true;
        harness.Batch.When(value => value.Commit()).Do(_ =>
        {
            if (failCommit) throw new InvalidOperationException("Injected write failure");
        });
        harness.Repository.TryAdd(PersistenceSnapshot(0, 1, harness.Pool));
        harness.Repository.TryAdd(PersistenceSnapshot(1, 2, harness.Pool));
        Assert.That(harness.Coordinator.PersistUpTo(PersistenceState(3)), Is.False);
        harness.Persistence.DidNotReceive().CreateWriteBatch(Arg.Any<StateId>(), Arg.Any<StateId>(), Arg.Any<ValueHash256>(), Arg.Any<WriteFlags>());
        Assert.Throws<InvalidOperationException>(() => harness.Coordinator.PersistUpTo(PersistenceState(2)));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(harness.Coordinator.GetCurrentPersistedStateId(), Is.EqualTo(PersistenceState(0)));
            Assert.That(harness.Repository.Count, Is.EqualTo(2));
        }
        harness.Batch.Received(1).Dispose();
        failCommit = false;
        Assert.That(harness.Coordinator.PersistUpTo(PersistenceState(2)), Is.True);
        Assert.That(harness.Coordinator.GetCurrentPersistedStateId(), Is.EqualTo(PersistenceState(2)));
    }

    [Test]
    public async Task PersistenceBackpressure_StallsProducer_AndShutdownDrains([Values] bool cancelProducer)
    {
        using CoordinatorHarness harness = new(new PbtConfig { CompactSize = 1, CompactionOffset = 0, MinReorgDepth = 0, MaxReorgDepth = 1 });
        PbtResourcePool pool = harness.Pool;
        PbtSnapshotRepository repository = harness.Repository;
        using CancellationTokenSource processExit = new();
        IProcessExitSource exitSource = Substitute.For<IProcessExitSource>();
        exitSource.Token.Returns(processExit.Token);
        TaskCompletionSource enteredPersistence = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releasePersistence = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource producerStalled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        List<StateId> persisted = [];
        harness.Persistence.CreateWriteBatch(Arg.Any<StateId>(), Arg.Any<StateId>(), Arg.Any<ValueHash256>(), Arg.Any<WriteFlags>())
            .Returns(call =>
            {
                enteredPersistence.TrySetResult();
                releasePersistence.Task.GetAwaiter().GetResult();
                persisted.Add(call.ArgAt<StateId>(1));
                return Substitute.For<IPbtPersistence.IWriteBatch>();
            });
        InterfaceLogger logger = Substitute.For<InterfaceLogger>();
        logger.IsWarn.Returns(true);
        logger.When(value => value.Warn(Arg.Any<string>())).Do(_ =>
        {
            if (repository.GetLastCommittedStateId() == PersistenceState(68)) producerStalled.TrySetResult();
        });
        ILogManager logs = Substitute.For<ILogManager>();
        ILogger wrappedLogger = new(logger);
        logs.GetClassLogger<PbtDbManager>().Returns(wrappedLogger);
        PbtDbManager manager = harness.CreateManager(exitSource, logs, NoopPbtTrieNodeCache.Instance);
        Task producer = Task.CompletedTask;
        try
        {
            manager.AddSnapshot(PersistenceSnapshot(0, 1, pool), pool.GetCachedResource(PbtResourcePool.Usage.MainBlockProcessing));
            manager.AddSnapshot(PersistenceSnapshot(1, 2, pool), pool.GetCachedResource(PbtResourcePool.Usage.MainBlockProcessing));
            await enteredPersistence.Task.WaitAsync(TimeSpan.FromSeconds(10));
            producer = Task.Run(() =>
            {
                for (int number = 3; number <= 68; number++)
                    manager.AddSnapshot(PersistenceSnapshot(number - 1, number, pool), pool.GetCachedResource(PbtResourcePool.Usage.MainBlockProcessing));
            });
            await producerStalled.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.That(producer.IsCompleted, Is.False);
            if (cancelProducer)
            {
                processExit.Cancel();
                await producer.WaitAsync(TimeSpan.FromSeconds(10));
            }
            if (cancelProducer)
            {
                Task disposal = manager.DisposeAsync().AsTask();
                Assert.That(disposal.IsCompleted, Is.False, "persistence must finish before shutdown completes");
                releasePersistence.TrySetResult();
                await disposal.WaitAsync(TimeSpan.FromSeconds(10));
            }
            else
            {
                releasePersistence.TrySetResult();
                await producer.WaitAsync(TimeSpan.FromSeconds(10));
                await manager.DisposeAsync();
            }
            await manager.DisposeAsync();
            using (Assert.EnterMultipleScope())
            {
                Assert.That(harness.Coordinator.GetCurrentPersistedStateId(), Is.EqualTo(PersistenceState(68)));
                Assert.That(persisted.Count, Is.EqualTo(68));
                Assert.That(repository.Count, Is.Zero);
            }
            for (int index = 0; index < persisted.Count; index++)
                Assert.That(persisted[index], Is.EqualTo(PersistenceState(index + 1)));
        }
        finally
        {
            releasePersistence.TrySetResult();
            await producer.WaitAsync(TimeSpan.FromSeconds(10));
            await manager.DisposeAsync();
        }
    }

    [Test]
    public async Task Disposal_FlushesStandaloneButPreservesMirrorFloor([Values] bool mirror)
    {
        SnapshotableMemColumnsDb<PbtColumns> db = new("pbt");
        Hash256 root;
        await using (PbtTestContext context = new(db, new PbtConfig { MirrorFlat = mirror }))
        {
            using (IWorldStateScopeProvider.IScope scope = context.CreateScopeProvider().BeginScope(null, new LocalMetrics()))
                root = CommitBlock(scope, 1, 100);
            await context.Manager.DisposeAsync();
            await context.Manager.DisposeAsync();
        }
        await using PbtTestContext reopened = new(db, new PbtConfig { MirrorFlat = mirror });
        using IPbtPersistence.IReader reader = reopened.Persistence.CreateReader();
        Assert.That(reader.CurrentState, Is.EqualTo(mirror ? StateId.PreGenesis : new StateId(1, root)));
    }

    public enum TransientHandOff { Admitted, NoCache, Duplicate, ChannelFull }

    [Test]
    public async Task AddSnapshot_hands_the_transient_to_the_populator_or_releases_it([Values] TransientHandOff mode)
    {
        using CoordinatorHarness harness = new(new PbtConfig { CompactionOffset = 0 });
        PbtResourcePool pool = harness.Pool;
        IProcessExitSource exitSource = Substitute.For<IProcessExitSource>();
        exitSource.Token.Returns(CancellationToken.None);
        using PbtTrieNodeCache cache = new(harness.Config);
        // A closed gate parks the populator on the first transient, so the second fills the one-slot queue and the third finds it full.
        using ManualResetEventSlim populatorGate = new(mode != TransientHandOff.ChannelFull);
        IPbtTrieNodeCache gatedCache = Substitute.For<IPbtTrieNodeCache>();
        gatedCache.When(c => c.Add(Arg.Any<PbtTransientResource>())).Do(call =>
        {
            populatorGate.Wait();
            cache.Add(call.Arg<PbtTransientResource>());
        });
        PbtDbManager manager = harness.CreateManager(exitSource, LimboLogs.Instance, mode == TransientHandOff.NoCache ? NoopPbtTrieNodeCache.Instance : gatedCache);
        ConcurrentBag<PbtTransientResource> returned = [];
        IPbtResourcePool recordingPool = Substitute.For<IPbtResourcePool>();
        recordingPool.When(p => p.ReturnCachedResource(Arg.Any<PbtResourcePool.Usage>(), Arg.Any<PbtTransientResource>())).Do(call =>
        {
            returned.Add(call.Arg<PbtTransientResource>());
            pool.ReturnCachedResource(call.Arg<PbtResourcePool.Usage>(), call.Arg<PbtTransientResource>());
        });
        PbtTransientResource first = StagedTransient(pool, recordingPool, 1);
        PbtTransientResource second = StagedTransient(pool, recordingPool, 2);
        PbtTransientResource third = StagedTransient(pool, recordingPool, 3);
        try
        {
            manager.AddSnapshot(PersistenceSnapshot(0, 1, pool), first);
            manager.AddSnapshot(PersistenceSnapshot(mode == TransientHandOff.Duplicate ? 0 : 1, mode == TransientHandOff.Duplicate ? 1 : 2, pool), second);
            if (mode == TransientHandOff.ChannelFull)
            {
                Task stalledCommit = Task.Run(() => manager.AddSnapshot(PersistenceSnapshot(2, 3, pool), third));
                Assert.That(stalledCommit.Wait(200), Is.False, "a full queue stalls the commit instead of dropping the staged groups");
                populatorGate.Set();
                Assert.That(stalledCommit.Wait(5000), Is.True, "the stalled commit resumes once the populator drains the queue");
            }
            else third.ReleaseLease();
            if (mode == TransientHandOff.Duplicate) Assert.That(returned, Does.Contain(second), "a transient that cannot reach the populator returns to the pool at once");
            Assert.That(() => cache.EntryCount, Is.EqualTo(mode switch { TransientHandOff.NoCache => 0, TransientHandOff.Duplicate => 1, TransientHandOff.Admitted => 2, _ => 3 }).After(5000, 10));
            Assert.That(() => returned, Is.EquivalentTo(new[] { first, second, third }).After(5000, 10), "every transient returns to the pool exactly once, ingested or refused");
            Assert.That(first.NodeGroups.Count + second.NodeGroups.Count + third.NodeGroups.Count, Is.Zero);
        }
        finally
        {
            await manager.DisposeAsync();
        }
    }

    /// <summary>Rents a transient that stages one group and returns itself through <paramref name="returnPool"/>.</summary>
    private static PbtTransientResource StagedTransient(PbtResourcePool pool, IPbtResourcePool returnPool, byte marker)
    {
        PbtTransientResource transient = pool.GetCachedResource(PbtResourcePool.Usage.MainBlockProcessing);
        transient.OnRented(returnPool, PbtResourcePool.Usage.MainBlockProcessing);
        using RefCountingMemory payload = RefCountingMemory.OwningRocksDb(new ArrayMemoryManager([marker]));
        transient.NodeGroups.Set(new ValueHash256(TestItem.KeccakA.Bytes), new PbtNodePath([marker], 8), payload);
        return transient;
    }

    [Test]
    public async Task ResetPersistedStateId_IsNotUndoneByAReaderOpenedBeforeIt()
    {
        using CoordinatorHarness harness = new(new PbtConfig());
        IPbtPersistence.IReader beforeImport = Substitute.For<IPbtPersistence.IReader>();
        beforeImport.CurrentState.Returns(StateId.PreGenesis);
        IPbtPersistence.IReader afterImport = Substitute.For<IPbtPersistence.IReader>();
        afterImport.CurrentState.Returns(PersistenceState(2));
        using ManualResetEventSlim opened = new();
        using ManualResetEventSlim release = new();
        // The first reader is a snapshot taken before an anchor import wrote, held open across the import's reset.
        harness.Persistence.CreateReader().Returns(_ => { opened.Set(); release.Wait(); return beforeImport; }, _ => afterImport);
        PbtPersistenceCoordinator coordinator = harness.Coordinator;

        Task<StateId> racing = Task.Run(coordinator.GetCurrentPersistedStateId);
        opened.Wait();
        coordinator.ResetPersistedStateId();
        release.Set();

        Assert.That(await racing, Is.EqualTo(PersistenceState(2)));
        Assert.That(coordinator.GetCurrentPersistedStateId(), Is.EqualTo(PersistenceState(2)));
    }

    private static StateId PersistenceState(int number) => new((ulong)number, TestItem.KeccakA.ValueHash256);

    /// <summary>A persistence coordinator over a mocked persistence whose reader starts at block 0 and whose write batches are <see cref="Batch"/>.</summary>
    private sealed class CoordinatorHarness : IDisposable
    {
        public CoordinatorHarness(PbtConfig config)
        {
            Config = config;
            Pool = new(config);
            Schedule = new(Metadata, config, LimboLogs.Instance);
            IPbtPersistence.IReader reader = Substitute.For<IPbtPersistence.IReader>();
            reader.CurrentState.Returns(PersistenceState(0));
            Persistence.CreateReader().Returns(reader);
            Persistence.CreateWriteBatch(Arg.Any<StateId>(), Arg.Any<StateId>(), Arg.Any<ValueHash256>(), Arg.Any<WriteFlags>()).Returns(Batch);
            Coordinator = new(config, Finalized, Persistence, Repository, Schedule, NullStatePersistenceBarrier.Instance, LimboLogs.Instance);
        }

        public PbtConfig Config { get; }
        public PbtResourcePool Pool { get; }
        public PbtSnapshotRepository Repository { get; } = new(new MetricsConfig());
        public MemDb Metadata { get; } = new();
        public PbtCompactionSchedule Schedule { get; }
        public PbtTestContext.TestFinalizedStateProvider Finalized { get; } = new();
        public IPbtPersistence Persistence { get; } = Substitute.For<IPbtPersistence>();
        public IPbtPersistence.IWriteBatch Batch { get; } = Substitute.For<IPbtPersistence.IWriteBatch>();
        public PbtPersistenceCoordinator Coordinator { get; }

        public PbtDbManager CreateManager(IProcessExitSource exitSource, ILogManager logs, IPbtTrieNodeCache trieNodeCache) =>
            new(Repository, Coordinator, Persistence, Pool, new PbtSnapshotCompactor(Pool, Schedule, Repository, Config), exitSource, logs, Config,
                new MetricsConfig(), trieNodeCache);

        public void Dispose()
        {
            Repository.RemoveStatesUntil(ulong.MaxValue);
            Metadata.Dispose();
        }
    }

    private static PbtSnapshot PersistenceSnapshot(int from, int to, PbtResourcePool pool) =>
        new(PersistenceState(from), PersistenceState(to), TestItem.KeccakA.ValueHash256,
            pool.GetSnapshotContent(PbtResourcePool.Usage.MainBlockProcessing), pool, PbtResourcePool.Usage.MainBlockProcessing);

}
