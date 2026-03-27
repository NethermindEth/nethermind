// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Config;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.State.Flat.Persistence;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test;

[TestFixture]
public class FlatDbManagerTests
{
    private IResourcePool _resourcePool = null!;
    private IProcessExitSource _processExitSource = null!;
    private ITrieNodeCache _trieNodeCache = null!;
    private ISnapshotCompactor _snapshotCompactor = null!;
    private ISnapshotRepository _snapshotRepository = null!;
    private IPersistenceManager _persistenceManager = null!;
    private IFlatDbConfig _config = null!;
    private CancellationTokenSource _cts = null!;

    [SetUp]
    public void SetUp()
    {
        _resourcePool = Substitute.For<IResourcePool>();
        _cts = new CancellationTokenSource();
        _processExitSource = Substitute.For<IProcessExitSource>();
        _processExitSource.Token.Returns(_cts.Token);
        _trieNodeCache = Substitute.For<ITrieNodeCache>();
        _snapshotCompactor = Substitute.For<ISnapshotCompactor>();
        _snapshotRepository = Substitute.For<ISnapshotRepository>();
        _persistenceManager = Substitute.For<IPersistenceManager>();
        _config = new FlatDbConfig { CompactSize = 16, MaxInFlightCompactJob = 4, InlineCompaction = true };
        Metrics.CompactorQueueFullCount = 0;
        Metrics.InlineDrainActivationCount = 0;
    }

    [TearDown]
    public void TearDown()
    {
        _cts.Cancel();
        _cts.Dispose();
    }

    private FlatDbManager CreateManager() => new(
        _resourcePool,
        _processExitSource,
        _trieNodeCache,
        _snapshotCompactor,
        _snapshotRepository,
        _persistenceManager,
        _config,
        LimboLogs.Instance,
        enableDetailedMetrics: false);

    private static StateId CreateStateId(long blockNumber, byte rootByte = 0)
    {
        byte[] bytes = new byte[32];
        bytes[0] = rootByte;
        return new StateId(blockNumber, new ValueHash256(bytes));
    }

    [Test]
    public async Task HasStateForBlock_FoundInRepository_ReturnsTrue()
    {
        StateId stateId = CreateStateId(10);
        _snapshotRepository.HasState(stateId).Returns(true);
        _persistenceManager.GetCurrentPersistedStateId().Returns(CreateStateId(5));

        await using FlatDbManager manager = CreateManager();
        bool result = manager.HasStateForBlock(stateId);

        Assert.That(result, Is.True);
    }

    [Test]
    public async Task HasStateForBlock_FoundInPersistence_ReturnsTrue()
    {
        StateId stateId = CreateStateId(10);
        _snapshotRepository.HasState(stateId).Returns(false);
        _persistenceManager.GetCurrentPersistedStateId().Returns(stateId);

        await using FlatDbManager manager = CreateManager();
        bool result = manager.HasStateForBlock(stateId);

        Assert.That(result, Is.True);
    }

    [Test]
    public async Task HasStateForBlock_NotFound_ReturnsFalse()
    {
        StateId stateId = CreateStateId(10);
        _snapshotRepository.HasState(stateId).Returns(false);
        _persistenceManager.GetCurrentPersistedStateId().Returns(CreateStateId(5));

        await using FlatDbManager manager = CreateManager();
        bool result = manager.HasStateForBlock(stateId);

        Assert.That(result, Is.False);
    }

    [Test]
    public async Task HasStateForBlock_EarlierBlockNumberWithoutExactStateId_ReturnsFalse()
    {
        StateId stateId = CreateStateId(9, rootByte: 9);
        _snapshotRepository.HasState(stateId).Returns(false);
        _persistenceManager.GetCurrentPersistedStateId().Returns(CreateStateId(10, rootByte: 10));

        await using FlatDbManager manager = CreateManager();
        bool result = manager.HasStateForBlock(stateId);

        Assert.That(result, Is.False);
    }

    [Test]
    public async Task AddSnapshot_BlockBelowPersistedState_ReturnsEarlyAndLogsWarning()
    {
        StateId persistedStateId = CreateStateId(100);
        _persistenceManager.GetCurrentPersistedStateId().Returns(persistedStateId);

        ResourcePool realResourcePool = new(_config);
        StateId snapshotFrom = CreateStateId(50);
        StateId snapshotTo = CreateStateId(51);
        Snapshot snapshot = realResourcePool.CreateSnapshot(snapshotFrom, snapshotTo, ResourcePool.Usage.MainBlockProcessing);
        TransientResource transientResource = realResourcePool.GetCachedResource(ResourcePool.Usage.MainBlockProcessing);

        await using FlatDbManager manager = CreateManager();
        manager.AddSnapshot(snapshot, transientResource);

        _snapshotRepository.DidNotReceive().TryAddSnapshot(Arg.Any<Snapshot>());
    }

    [Test]
    public async Task AddSnapshot_ValidSnapshot_AddsToRepository()
    {
        StateId persistedStateId = CreateStateId(5);
        _persistenceManager.GetCurrentPersistedStateId().Returns(persistedStateId);
        _snapshotRepository.TryAddSnapshot(Arg.Any<Snapshot>()).Returns(true);

        ResourcePool realResourcePool = new(_config);
        StateId snapshotFrom = CreateStateId(10);
        StateId snapshotTo = CreateStateId(11);
        Snapshot snapshot = realResourcePool.CreateSnapshot(snapshotFrom, snapshotTo, ResourcePool.Usage.MainBlockProcessing);
        TransientResource transientResource = realResourcePool.GetCachedResource(ResourcePool.Usage.MainBlockProcessing);

        await using FlatDbManager manager = CreateManager();
        manager.AddSnapshot(snapshot, transientResource);

        _snapshotRepository.Received(1).TryAddSnapshot(snapshot);
    }

    [Test]
    public async Task GatherReadOnlySnapshotBundle_CacheClearedPeriodically()
    {
        StateId stateId = CreateStateId(10);

        IPersistence.IPersistenceReader mockReader = Substitute.For<IPersistence.IPersistenceReader>();
        mockReader.CurrentState.Returns(stateId);

        _persistenceManager.LeaseReader().Returns(mockReader);
        _snapshotRepository.AssembleSnapshots(stateId, stateId, Arg.Any<int>())
            .Returns(new SnapshotPooledList(0));

        await using FlatDbManager manager = CreateManager();

        // First call populates the cache
        using (ReadOnlySnapshotBundle bundle1 = manager.GatherReadOnlySnapshotBundle(stateId)) { }

        // Second call should hit cache (no new LeaseReader call)
        _persistenceManager.ClearReceivedCalls();
        using (ReadOnlySnapshotBundle bundle2 = manager.GatherReadOnlySnapshotBundle(stateId)) { }
        _persistenceManager.DidNotReceive().LeaseReader();

        // Wait for periodic clear (15s + margin)
        await Task.Delay(TimeSpan.FromSeconds(17));

        // After cache clear, next call needs a new reader
        _persistenceManager.ClearReceivedCalls();
        using (ReadOnlySnapshotBundle bundle3 = manager.GatherReadOnlySnapshotBundle(stateId)) { }
        _persistenceManager.Received(1).LeaseReader();
    }

    [Test]
    public async Task AddSnapshot_DuplicateSnapshot_DisposesSnapshotAndReturnsResource()
    {
        StateId persistedStateId = CreateStateId(5);
        _persistenceManager.GetCurrentPersistedStateId().Returns(persistedStateId);
        _snapshotRepository.TryAddSnapshot(Arg.Any<Snapshot>()).Returns(false);

        ResourcePool realResourcePool = new(_config);
        StateId snapshotFrom = CreateStateId(10);
        StateId snapshotTo = CreateStateId(11);
        Snapshot snapshot = realResourcePool.CreateSnapshot(snapshotFrom, snapshotTo, ResourcePool.Usage.MainBlockProcessing);
        TransientResource transientResource = realResourcePool.GetCachedResource(ResourcePool.Usage.MainBlockProcessing);

        await using FlatDbManager manager = CreateManager();
        manager.AddSnapshot(snapshot, transientResource);

        _resourcePool.Received(1).ReturnCachedResource(ResourcePool.Usage.MainBlockProcessing, transientResource);
    }

    [Test]
    public async Task AddSnapshot_WhenCompactorQueueIsFull_CompletesWithinBoundedTime()
    {
        (FlatDbManager manager, TaskCompletionSource releaseCompaction, Snapshot snapshot3, TransientResource resource3) =
            await SetupSaturatedCompactorQueue();
        await using (manager)
        {
            Task thirdAddSnapshot = Task.Run(() => manager.AddSnapshot(snapshot3, resource3));

            try
            {
                Task completedTask = await Task.WhenAny(thirdAddSnapshot, Task.Delay(TimeSpan.FromMilliseconds(500)));
                bool completed = completedTask == thirdAddSnapshot;

                Assert.That(completed, Is.True, "AddSnapshot should not remain blocked when the compactor queue is saturated.");
            }
            finally
            {
                releaseCompaction.TrySetResult();
                await thirdAddSnapshot.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
    }

    [Test]
    public async Task AddSnapshot_WhenQueueSaturated_EmitsBackpressureSignal()
    {
        (FlatDbManager manager, TaskCompletionSource releaseCompaction, Snapshot snapshot3, TransientResource resource3) =
            await SetupSaturatedCompactorQueue();
        await using (manager)
        {
            manager.AddSnapshot(snapshot3, resource3);

            Assert.That(Metrics.CompactorQueueFullCount, Is.EqualTo(1));
            Assert.That(Metrics.InlineDrainActivationCount, Is.EqualTo(1));

            releaseCompaction.TrySetResult();
        }
    }

    /// <summary>
    /// Creates a FlatDbManager with MaxInFlightCompactJob=1, fills the queue with two snapshots (first blocks compaction),
    /// and returns the manager, a release handle, and a third snapshot ready to trigger backpressure.
    /// </summary>
    private async Task<(FlatDbManager Manager, TaskCompletionSource ReleaseCompaction, Snapshot Snapshot3, TransientResource Resource3)> SetupSaturatedCompactorQueue()
    {
        _config = new FlatDbConfig { CompactSize = 16, MaxInFlightCompactJob = 1, InlineCompaction = false };
        _persistenceManager.GetCurrentPersistedStateId().Returns(CreateStateId(0));
        _snapshotRepository.TryAddSnapshot(Arg.Any<Snapshot>()).Returns(true);

        TaskCompletionSource firstCompactionStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseCompaction = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int compactionCallCount = 0;
        _snapshotCompactor.DoCompactSnapshot(Arg.Any<StateId>())
            .Returns(_ =>
            {
                int callCount = Interlocked.Increment(ref compactionCallCount);
                if (callCount == 1)
                {
                    firstCompactionStarted.TrySetResult();
                    releaseCompaction.Task.GetAwaiter().GetResult();
                }

                return false;
            });

        ResourcePool realResourcePool = new(_config);
        (Snapshot snapshot1, TransientResource resource1) = CreateSnapshot(realResourcePool, 1, 2);
        (Snapshot snapshot2, TransientResource resource2) = CreateSnapshot(realResourcePool, 2, 3);
        (Snapshot snapshot3, TransientResource resource3) = CreateSnapshot(realResourcePool, 3, 4);

        FlatDbManager manager = CreateManager();
        manager.AddSnapshot(snapshot1, resource1);
        await firstCompactionStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        manager.AddSnapshot(snapshot2, resource2);

        return (manager, releaseCompaction, snapshot3, resource3);
    }

    private static (Snapshot Snapshot, TransientResource Resource) CreateSnapshot(ResourcePool resourcePool, long fromBlock, long toBlock)
    {
        StateId snapshotFrom = CreateStateId(fromBlock);
        StateId snapshotTo = CreateStateId(toBlock);
        Snapshot snapshot = resourcePool.CreateSnapshot(snapshotFrom, snapshotTo, ResourcePool.Usage.MainBlockProcessing);
        TransientResource transientResource = resourcePool.GetCachedResource(ResourcePool.Usage.MainBlockProcessing);
        return (snapshot, transientResource);
    }
}
