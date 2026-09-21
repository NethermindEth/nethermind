// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Flat.PersistedSnapshots;
using Nethermind.Trie.Pruning;
using Nethermind.Trie;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test;

[TestFixture]
public class FlatDbManagerPersistedTests
{
    private static readonly TimeSpan DisposeWaitLimit = TimeSpan.FromSeconds(10);

    private string _testDir = null!;
    private ResourcePool _pool = null!;
    private IProcessExitSource _processExitSource = null!;
    private CancellationTokenSource _cts = null!;
    private FlatDbConfig _config = null!;

    [SetUp]
    public void SetUp()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"nethermind_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDir);
        _pool = new ResourcePool(new FlatDbConfig());
        _cts = new CancellationTokenSource();
        _processExitSource = Substitute.For<IProcessExitSource>();
        _processExitSource.Token.Returns(_cts.Token);
        _config = new FlatDbConfig { CompactSize = 16, MaxInFlightCompactJob = 4, InlineCompaction = true };
    }

    [TearDown]
    public void TearDown()
    {
        _cts.Cancel();
        _cts.Dispose();
        if (Directory.Exists(_testDir))
            Directory.Delete(_testDir, recursive: true);
    }

    [Test]
    public async Task GatherReadOnlySnapshotBundle_IncludesPersistedSnapshots()
    {
        StateId s0 = new(0, Keccak.EmptyTreeHash);
        StateId s1 = new(1, Keccak.Compute("1"));

        TreePath path = new(Keccak.Compute("path"), 4);
        byte[] nodeRlp = [0xC2, 0x80, 0x80];
        SnapshotContent content = new();
        content.StateNodes[path] = new TrieNode(NodeType.Leaf, nodeRlp);
        Snapshot snap = new(s0, s1, content, _pool, ResourcePool.Usage.MainBlockProcessing);

        using FlatTestContainer tier = new(arenaFileSizeBytes: 4096);
        SnapshotRepository repo = tier.Repository;
        tier.ConvertToPersistedBase(snap).Dispose();

        // Persisted snapshot covers s0→s1; mock reader anchored at s0 so the manager sees it as the persisted base.
        IPersistenceManager persistenceManager = Substitute.For<IPersistenceManager>();
        IPersistence.IPersistenceReader reader = Substitute.For<IPersistence.IPersistenceReader>();
        reader.CurrentState.Returns(s0);
        persistenceManager.LeaseReader().Returns(reader);
        persistenceManager.GetCurrentPersistedStateId().Returns(s0);

        await using FlatDbManager manager = new(
            Substitute.For<IResourcePool>(),
            _processExitSource,
            Substitute.For<ITrieNodeCache>(),
            Substitute.For<ISnapshotCompactor>(),
            repo,
            persistenceManager,
            Substitute.For<IPersistedSnapshotLoader>(),
            _config,
            new BlocksConfig(),
            LimboLogs.Instance,
            enableDetailedMetrics: false);

        ReadOnlySnapshotBundle bundle = manager.GatherReadOnlySnapshotBundle(s1);

        byte[]? result = bundle.TryLoadStateRlp(path, Keccak.Compute("hash"), ReadFlags.None);
        Assert.That(result, Is.EqualTo(nodeRlp));

        bundle.Dispose();
    }

    [Test]
    public void DisposeAsync_CompletesPromptlyAndIsIdempotent()
    {
        using FlatTestContainer tier = new(arenaFileSizeBytes: 4096);
        SnapshotRepository repo = tier.Repository;

        StateId s0 = new(0, Keccak.EmptyTreeHash);
        StateId s1 = new(1, Keccak.Compute("1"));
        SnapshotContent content = new();
        content.Accounts[TestItem.AddressA] = Build.An.Account.WithBalance(1).TestObject;
        tier.ConvertToPersistedBase(new Snapshot(s0, s1, content, _pool, ResourcePool.Usage.MainBlockProcessing)).Dispose();

        FlatDbManager manager = new(
            Substitute.For<IResourcePool>(),
            _processExitSource,
            Substitute.For<ITrieNodeCache>(),
            Substitute.For<ISnapshotCompactor>(),
            repo,
            Substitute.For<IPersistenceManager>(),
            Substitute.For<IPersistedSnapshotLoader>(),
            _config,
            new BlocksConfig(),
            LimboLogs.Instance,
            enableDetailedMetrics: false);

        // WaitAsync bounds only the wait, not the drain. A wedged worker-channel drain
        // causes a fast TimeoutException here instead of a stalled test host.
        Assert.DoesNotThrowAsync(async () => await manager.DisposeAsync().AsTask().WaitAsync(DisposeWaitLimit));

        // A second disposal must be a no-op. A completed channel throws on Complete().
        Assert.DoesNotThrowAsync(async () => await manager.DisposeAsync().AsTask().WaitAsync(DisposeWaitLimit));
    }

    [TestCase(0, TestName = "DisposeAsync_WhenPersistenceSucceeds_DrainsQueuedCompactions")]
    [TestCase(1, TestName = "DisposeAsync_WhenPersistenceIsCanceled_ReleasesBlockedCompactor")]
    [TestCase(2, TestName = "DisposeAsync_WhenAPersistFails_ReleasesBlockedCompactorAndPersistsTheRest")]
    public async Task DisposeAsync_WhenPersistenceStops_ReleasesBlockedCompactor(int completionMode)
    {
        // Hold the first persist, fill its queue, then finish, cancel or fail it with the next compaction waiting for space.
        _config.InlineCompaction = false;
        using FlatTestContainer tier = new(_config, arenaFileSizeBytes: 4096);
        TaskCompletionSource persistenceStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource persistenceResult = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource lastCompactionStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        IPersistenceManager persistence = Substitute.For<IPersistenceManager>();
        persistence.GetCurrentPersistedStateId().Returns(StateId.PreGenesis);
        persistence.AddToPersistence(Arg.Any<StateId>()).Returns(_ =>
        {
            persistenceStarted.TrySetResult();
            return persistenceResult.Task;
        });
        ISnapshotCompactor compactor = Substitute.For<ISnapshotCompactor>();
        ulong blockedBlock = (ulong)_config.MaxInFlightCompactJob + 2;
        compactor.DoCompactSnapshot(Arg.Any<StateId>()).Returns(call =>
        {
            if (call.Arg<StateId>().BlockNumber == blockedBlock) lastCompactionStarted.TrySetResult();
            return false;
        });
        FlatDbManager manager = new(tier.ResourcePool, _processExitSource,
            Substitute.For<ITrieNodeCache>(), compactor, tier.Repository, persistence,
            Substitute.For<IPersistedSnapshotLoader>(), _config, new BlocksConfig(), LimboLogs.Instance, false);

        Task? disposal = null;
        try
        {
            StateId previous = new(0, Keccak.EmptyTreeHash);
            for (ulong number = 1; number <= blockedBlock; number++)
            {
                StateId next = new(number, Keccak.Compute(number.ToString()));
                Commit(manager, tier.ResourcePool, previous, next, 1);
                previous = next;
                if (number == 1) await persistenceStarted.Task.WaitAsync(DisposeWaitLimit);
            }
            await lastCompactionStarted.Task.WaitAsync(DisposeWaitLimit);
            Assert.That(persistenceResult.Task.IsCompleted, Is.False, "the consumer must still hold the first persist");
            disposal = manager.DisposeAsync().AsTask();
            Assert.That(disposal.IsCompleted, Is.False, "the pending persist must keep disposal waiting");

            if (completionMode == 1)
            {
                _cts.Cancel();
                persistenceResult.SetCanceled(_cts.Token);
                await disposal.WaitAsync(DisposeWaitLimit);
            }
            else
            {
                if (completionMode == 2) persistenceResult.SetException(new IOException("persist failed"));
                else persistenceResult.SetResult();
                await disposal.WaitAsync(DisposeWaitLimit);
                await persistence.Received((int)blockedBlock).AddToPersistence(Arg.Any<StateId>());
            }
            Assert.That(disposal.IsCompleted, Is.True, "a stopped consumer must not strand the compactor");
        }
        finally
        {
            persistenceResult.TrySetCanceled();
            disposal ??= manager.DisposeAsync().AsTask();
            await Task.WhenAny(disposal, Task.Delay(DisposeWaitLimit));
            _ = disposal.Exception;
        }
    }

    [Test]
    public async Task AddSnapshot_WhenACompactionFails_CompactsAndPersistsTheNextSnapshot()
    {
        _config.InlineCompaction = false;
        using FlatTestContainer tier = new(_config, arenaFileSizeBytes: 4096);
        IPersistenceManager persistence = Substitute.For<IPersistenceManager>();
        persistence.GetCurrentPersistedStateId().Returns(StateId.PreGenesis);
        persistence.AddToPersistence(Arg.Any<StateId>()).Returns(Task.CompletedTask);
        StateId failing = new(1, Keccak.Compute("1"));
        StateId next = new(2, Keccak.Compute("2"));
        ISnapshotCompactor compactor = Substitute.For<ISnapshotCompactor>();
        compactor.DoCompactSnapshot(failing).Returns(_ => throw new IOException("compaction failed"));
        FlatDbManager manager = new(tier.ResourcePool, _processExitSource,
            Substitute.For<ITrieNodeCache>(), compactor, tier.Repository, persistence,
            Substitute.For<IPersistedSnapshotLoader>(), _config, new BlocksConfig(), LimboLogs.Instance, false);

        Commit(manager, tier.ResourcePool, new StateId(0, Keccak.EmptyTreeHash), failing, 1);
        Commit(manager, tier.ResourcePool, failing, next, 2);
        await manager.DisposeAsync().AsTask().WaitAsync(DisposeWaitLimit);

        await persistence.DidNotReceive().AddToPersistence(failing);
        await persistence.Received(1).AddToPersistence(next);
    }

    // The head-reset sequence: commit a branch, reset to its base, commit again from the base. The abandoned
    // branch must be gone and the new one served, through the real repository, compactor and persistence manager.
    [Test]
    public async Task DropStateNotReachableFrom_ThenCommitOnResetHead_DropsAbandonedBranchAndServesNewOne()
    {
        StateId head = new(0, Keccak.EmptyTreeHash);
        StateId abandoned1 = new(1, Keccak.Compute("a1"));
        StateId abandoned2 = new(2, Keccak.Compute("a2"));
        StateId reprocessed1 = new(1, Keccak.Compute("b1"));

        using FlatTestContainer tier = new(_config, arenaFileSizeBytes: 4096);
        IPersistence.IPersistenceReader reader = Substitute.For<IPersistence.IPersistenceReader>();
        reader.CurrentState.Returns(head);
        IPersistence persistence = Substitute.For<IPersistence>();
        persistence.CreateReader().Returns(reader);
        using PersistenceManager persistenceManager = new(
            _config,
            tier.Resolve<ICompactionSchedule>(),
            tier.Resolve<IFinalizedStateProvider>(),
            persistence,
            tier.Repository,
            NullStatePersistenceBarrier.Instance,
            LimboLogs.Instance,
            Substitute.For<IPersistedSnapshotCompactor>(),
            tier.Loader,
            _processExitSource);
        await using FlatDbManager manager = new(
            tier.ResourcePool,
            _processExitSource,
            tier.Resolve<ITrieNodeCache>(),
            tier.Resolve<ISnapshotCompactor>(),
            tier.Repository,
            persistenceManager,
            Substitute.For<IPersistedSnapshotLoader>(),
            _config,
            new BlocksConfig(),
            LimboLogs.Instance,
            enableDetailedMetrics: false);

        Commit(manager, tier.ResourcePool, head, abandoned1, balance: 1);
        Commit(manager, tier.ResourcePool, abandoned1, abandoned2, balance: 2);

        manager.DropStateNotReachableFrom(head);
        Commit(manager, tier.ResourcePool, head, reprocessed1, balance: 3);

        using ReadOnlySnapshotBundle bundle = manager.GatherReadOnlySnapshotBundle(reprocessed1);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(manager.HasStateForBlock(abandoned1), Is.False);
            Assert.That(manager.HasStateForBlock(abandoned2), Is.False);
            Assert.That(manager.HasStateForBlock(reprocessed1), Is.True);
            Assert.That(bundle.GetAccount(TestItem.AddressA)?.Balance, Is.EqualTo((UInt256)3));
        }
    }

    private static void Commit(FlatDbManager manager, ResourcePool pool, StateId from, StateId to, ulong balance)
    {
        Snapshot snapshot = pool.CreateSnapshot(from, to, ResourcePool.Usage.MainBlockProcessing);
        snapshot.Content.Accounts[TestItem.AddressA] = Build.An.Account.WithBalance(balance).TestObject;
        manager.AddSnapshot(snapshot, pool.GetCachedResource(ResourcePool.Usage.MainBlockProcessing));
    }
}
