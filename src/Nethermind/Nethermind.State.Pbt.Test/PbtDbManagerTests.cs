// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Threading;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Evm.State;
using Nethermind.Int256;
using NUnit.Framework;
using NSubstitute;
using Nethermind.Logging;
using Nethermind.Monitoring.Config;
using Nethermind.State.Pbt.Persistence;
using Nethermind.Trie.Pruning;

namespace Nethermind.State.Pbt.Test;

public class PbtDbManagerTests
{
    private static readonly Address Address = TestItem.AddressA;

    /// <summary>The per-block storage slot, on a stem separate from the account header.</summary>
    private static readonly UInt256 Slot = 1000;

    private static Hash256 CommitBlock(IWorldStateScopeProvider.IScope scope, ulong blockNumber, in UInt256 balance)
    {
        using (IWorldStateScopeProvider.IWorldStateWriteBatch batch = scope.StartWriteBatch(1))
        {
            batch.Set(Address, new Account(blockNumber, balance));
            using IWorldStateScopeProvider.IStorageWriteBatch storage = batch.CreateStorageWriteBatch(Address, 1);
            storage.Set(Slot, [(byte)blockNumber]);
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
        Assert.That(first.GetAccount(Address)!.Balance, Is.EqualTo((UInt256)100));

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
            Assert.That(scope.CreateStorageTree(Address).Get(Slot), Is.EqualTo((byte[])[3]), "and the slot decodes out of its own persisted blob");
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

    /// <summary>Restores state by the header root while continuing from the persisted tree root.</summary>
    [Test]
    public async Task PersistedState_IsKeyedByTheHeaderRoot_WithTheTreeRootRecordedBesideIt()
    {
        SnapshotableMemColumnsDb<PbtColumns> db = new("pbt");
        PbtTestChildHeaders childHeaders = new();
        BlockHeader first;
        ValueHash256 treeRoot;

        await using (PbtTestContext ctx = new(db, childHeaders: childHeaders))
        {
            // Block 0 has no header root, so genesis claims its tree root.
            BlockHeader genesis;
            using (IWorldStateScopeProvider.IScope genesisScope = ctx.CreateScopeProvider().BeginScope(null, new LocalMetrics()))
            {
                genesis = Header(0, CommitBlock(genesisScope, 0, 1));
            }

            first = childHeaders.Add(genesis, TestItem.KeccakA);
            using (IWorldStateScopeProvider.IScope scope = ctx.CreateScopeProvider().BeginScope(genesis, new LocalMetrics()))
            {
                Assert.That(CommitBlock(scope, 1, 100), Is.EqualTo(TestItem.KeccakA), "the block reports the root its header claims");
            }

            using (PbtReadOnlySnapshotBundle bundle = ((IPbtDbManager)ctx.Manager).GatherReadOnlyBundle(new StateId(first)))
            {
                treeRoot = bundle.TreeRoot;
            }

            Assert.That(treeRoot, Is.Not.EqualTo(TestItem.KeccakA.ValueHash256), "which is not the root the tree folded to");
            ctx.Manager.FlushCache(default);
        }

        await using (PbtTestContext reopened = new(db, childHeaders: childHeaders))
        {
            using (Persistence.IPbtPersistence.IReader reader = reopened.Persistence.CreateReader())
            {
                Assert.That(reader.CurrentState, Is.EqualTo(new StateId(first)));
                Assert.That(reader.CurrentRoot, Is.EqualTo(treeRoot));
            }

            BlockHeader second = childHeaders.Add(first, TestItem.KeccakB);
            using IWorldStateScopeProvider.IScope scope = reopened.CreateScopeProvider().BeginScope(first, new LocalMetrics());
            Assert.That(scope.Get(Address)!.Balance, Is.EqualTo((UInt256)100), "the persisted state is found by its header");
            Assert.That(CommitBlock(scope, 2, 200), Is.EqualTo(second.StateRoot), "and the branch carries on from it");
        }
    }
    [TestCase(32, 0)]
    [TestCase(8, 0)]
    [TestCase(1, 0)]
    [TestCase(32, 1)]
    [TestCase(1, 1)]
    [TestCase(32, 2)]
    [TestCase(1, 2)]
    [TestCase(32, 3)]
    [TestCase(1, 3)]
    public void Persistence_PrefersExistingUnits_AndBoundsBackgroundDrain(int width, int mode)
    {
        PbtConfig config = new() { CompactSize = 32, CompactionOffset = 0, MinReorgDepth = 0, MaxReorgDepth = 32, MirrorFlat = mode == 2 };
        PbtResourcePool pool = new(config);
        PbtSnapshotRepository repository = new();
        using MemDb metadata = new();
        PbtCompactionSchedule schedule = new(metadata, config, LimboLogs.Instance);
        PbtTestContext.TestFinalizedStateProvider finalized = new();
        IPbtPersistence persistence = Substitute.For<IPbtPersistence>();
        IPbtPersistence.IReader reader = Substitute.For<IPbtPersistence.IReader>();
        reader.CurrentState.Returns(PersistenceState(0));
        persistence.CreateReader().Returns(reader);
        List<(StateId From, StateId To)> writes = [];
        persistence.CreateWriteBatch(Arg.Any<StateId>(), Arg.Any<StateId>(), Arg.Any<ValueHash256>(), Arg.Any<WriteFlags>())
            .Returns(call =>
            {
                StateId from = call.ArgAt<StateId>(0);
                StateId to = call.ArgAt<StateId>(1);
                Assert.That(call.ArgAt<ValueHash256>(2), Is.EqualTo(to.StateRoot));
                writes.Add((from, to));
                return Substitute.For<IPbtPersistence.IWriteBatch>();
            });
        PbtPersistenceCoordinator coordinator = new(config, finalized, persistence, repository, schedule, NullStatePersistenceBarrier.Instance, LimboLogs.Instance);
        try
        {
            for (int number = 1; number <= 192; number++)
            {
                repository.TryAdd(PersistenceSnapshot(number - 1, number, pool));
                finalized.SetCanonicalRoot((ulong)number, TestItem.KeccakA);
                if (width > 1 && number % width == 0)
                    repository.TryAddCompacted(PersistenceSnapshot(number - width, number, pool));
            }
            if (mode == 3) finalized.FinalizedBlockNumber = 192;
            if (mode is 0 or 3) coordinator.CheckPersistence(PersistenceState(192));
            else if (mode == 1) coordinator.FlushToPersistence();
            else Assert.That(coordinator.PersistUpTo(PersistenceState(192)), Is.True);

            Assert.That(writes.Count, Is.EqualTo(mode is 0 or 3 ? 4 : 192 / width));
            for (int index = 0; index < writes.Count; index++)
                Assert.That(writes[index], Is.EqualTo((PersistenceState(index * width), PersistenceState((index + 1) * width))));
            Assert.That(coordinator.GetCurrentPersistedStateId(), Is.EqualTo(writes[^1].To));
        }
        finally
        {
            repository.RemoveStatesUntil(ulong.MaxValue);
        }
    }

    [Test]
    public void Persistence_FailedCommitDoesNotPublishOrPrune_AndUnknownMirrorSeedDoesNotAdvance()
    {
        PbtConfig config = new() { CompactSize = 2, CompactionOffset = 0, MirrorFlat = true };
        PbtResourcePool pool = new(config);
        PbtSnapshotRepository repository = new();
        using MemDb metadata = new();
        IPbtPersistence persistence = Substitute.For<IPbtPersistence>();
        IPbtPersistence.IReader reader = Substitute.For<IPbtPersistence.IReader>();
        reader.CurrentState.Returns(PersistenceState(0));
        persistence.CreateReader().Returns(reader);
        IPbtPersistence.IWriteBatch batch = Substitute.For<IPbtPersistence.IWriteBatch>();
        persistence.CreateWriteBatch(Arg.Any<StateId>(), Arg.Any<StateId>(), Arg.Any<ValueHash256>(), Arg.Any<WriteFlags>()).Returns(batch);
        bool failCommit = true;
        batch.When(value => value.Commit()).Do(_ =>
        {
            if (failCommit) throw new InvalidOperationException("Injected write failure");
        });
        PbtPersistenceCoordinator coordinator = new(config, new PbtTestContext.TestFinalizedStateProvider(), persistence, repository,
            new PbtCompactionSchedule(metadata, config, LimboLogs.Instance), NullStatePersistenceBarrier.Instance, LimboLogs.Instance);
        try
        {
            repository.TryAdd(PersistenceSnapshot(0, 1, pool));
            repository.TryAdd(PersistenceSnapshot(1, 2, pool));
            Assert.That(coordinator.PersistUpTo(PersistenceState(3)), Is.False);
            persistence.DidNotReceive().CreateWriteBatch(Arg.Any<StateId>(), Arg.Any<StateId>(), Arg.Any<ValueHash256>(), Arg.Any<WriteFlags>());
            Assert.Throws<InvalidOperationException>(() => coordinator.PersistUpTo(PersistenceState(2)));
            using (Assert.EnterMultipleScope())
            {
                Assert.That(coordinator.GetCurrentPersistedStateId(), Is.EqualTo(PersistenceState(0)));
                Assert.That(repository.Count, Is.EqualTo(2));
            }
            batch.Received(1).Dispose();
            failCommit = false;
            Assert.That(coordinator.PersistUpTo(PersistenceState(2)), Is.True);
            Assert.That(coordinator.GetCurrentPersistedStateId(), Is.EqualTo(PersistenceState(2)));
        }
        finally
        {
            repository.RemoveStatesUntil(ulong.MaxValue);
        }
    }

    [Test]
    public async Task PersistenceBackpressure_StallsProducer_AndShutdownDrains([Values] bool cancelProducer)
    {
        PbtConfig config = new() { CompactSize = 1, CompactionOffset = 0, MinReorgDepth = 0, MaxReorgDepth = 1 };
        PbtResourcePool pool = new(config);
        PbtSnapshotRepository repository = new();
        using MemDb metadata = new();
        using CancellationTokenSource processExit = new();
        IProcessExitSource exitSource = Substitute.For<IProcessExitSource>();
        exitSource.Token.Returns(processExit.Token);
        IPbtPersistence persistence = Substitute.For<IPbtPersistence>();
        IPbtPersistence.IReader reader = Substitute.For<IPbtPersistence.IReader>();
        reader.CurrentState.Returns(PersistenceState(0));
        persistence.CreateReader().Returns(reader);
        TaskCompletionSource enteredPersistence = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releasePersistence = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource producerStalled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        List<StateId> persisted = [];
        persistence.CreateWriteBatch(Arg.Any<StateId>(), Arg.Any<StateId>(), Arg.Any<ValueHash256>(), Arg.Any<WriteFlags>())
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
        PbtCompactionSchedule schedule = new(metadata, config, LimboLogs.Instance);
        PbtPersistenceCoordinator coordinator = new(config, new PbtTestContext.TestFinalizedStateProvider(), persistence,
            repository, schedule, NullStatePersistenceBarrier.Instance, LimboLogs.Instance);
        PbtDbManager manager = new(repository, coordinator, persistence, pool,
            new PbtSnapshotCompactor(pool, schedule, repository, config), exitSource, logs, config, new MetricsConfig());
        Task producer = Task.CompletedTask;
        try
        {
            manager.AddSnapshot(PersistenceSnapshot(0, 1, pool));
            manager.AddSnapshot(PersistenceSnapshot(1, 2, pool));
            await enteredPersistence.Task.WaitAsync(TimeSpan.FromSeconds(10));
            producer = Task.Run(() =>
            {
                for (int number = 3; number <= 68; number++)
                    manager.AddSnapshot(PersistenceSnapshot(number - 1, number, pool));
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
                Assert.That(coordinator.GetCurrentPersistedStateId(), Is.EqualTo(PersistenceState(68)));
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
            repository.RemoveStatesUntil(ulong.MaxValue);
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

    private static StateId PersistenceState(int number) => new((ulong)number, TestItem.KeccakA.ValueHash256);

    private static PbtSnapshot PersistenceSnapshot(int from, int to, PbtResourcePool pool) =>
        new(PersistenceState(from), PersistenceState(to), TestItem.KeccakA.ValueHash256,
            pool.GetSnapshotContent(PbtResourcePool.Usage.MainBlockProcessing), pool, PbtResourcePool.Usage.MainBlockProcessing);

}
