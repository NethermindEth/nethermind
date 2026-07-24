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
using Nethermind.State.Flat.PersistedSnapshots;
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
    private IPersistedSnapshotLoader _persistedSnapshotLoader = null!;
    private IFlatDbConfig _config = null!;
    private IBlocksConfig _blocksConfig = null!;
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
        _persistedSnapshotLoader = Substitute.For<IPersistedSnapshotLoader>();
        _config = new FlatDbConfig { CompactSize = 16, MaxInFlightCompactJob = 4, InlineCompaction = true };
        _blocksConfig = Substitute.For<IBlocksConfig>();
        _blocksConfig.SecondsPerSlot.Returns(12UL);
    }

    [TearDown]
    public void TearDown()
    {
        _persistedSnapshotLoader.Dispose();
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
        _persistedSnapshotLoader,
        _config,
        _blocksConfig,
        LimboLogs.Instance,
        enableDetailedMetrics: false);

    private static StateId CreateStateId(ulong blockNumber, byte rootByte = 0)
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

        _snapshotRepository.DidNotReceive().TryAdd(Arg.Any<Snapshot>(), SnapshotTier.InMemoryBase);
        _snapshotRepository.DidNotReceive().SetLastCommittedStateId(Arg.Any<StateId>());
    }

    [Test]
    public async Task AddSnapshot_ValidSnapshot_AddsToRepository()
    {
        StateId persistedStateId = CreateStateId(5);
        _persistenceManager.GetCurrentPersistedStateId().Returns(persistedStateId);
        _snapshotRepository.TryAdd(Arg.Any<Snapshot>(), SnapshotTier.InMemoryBase).Returns(true);

        ResourcePool realResourcePool = new(_config);
        StateId snapshotFrom = CreateStateId(10);
        StateId snapshotTo = CreateStateId(11);
        Snapshot snapshot = realResourcePool.CreateSnapshot(snapshotFrom, snapshotTo, ResourcePool.Usage.MainBlockProcessing);
        TransientResource transientResource = realResourcePool.GetCachedResource(ResourcePool.Usage.MainBlockProcessing);

        await using FlatDbManager manager = CreateManager();
        manager.AddSnapshot(snapshot, transientResource);

        _snapshotRepository.Received(1).TryAdd(snapshot, SnapshotTier.InMemoryBase);
        _snapshotRepository.Received(1).SetLastCommittedStateId(snapshotTo);
    }

    [Test]
    public async Task GatherReadOnlySnapshotBundle_CacheClearedPeriodically()
    {
        StateId stateId = CreateStateId(10);

        IPersistence.IPersistenceReader mockReader = Substitute.For<IPersistence.IPersistenceReader>();
        mockReader.CurrentState.Returns(stateId);

        _persistenceManager.LeaseReader().Returns(mockReader);
        _snapshotRepository.AssembleSnapshots(stateId, stateId, Arg.Any<int>())
            .Returns(new AssembledSnapshotResult(new SnapshotPooledList(0), PersistedSnapshotList.Empty()));

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
    public async Task PersistJob_WithoutPersistedStateAdvance_KeepsReadBundleCache()
    {
        StateId gatherStateId = CreateStateId(10);
        SetUpGather(gatherStateId);
        _persistenceManager.GetCurrentPersistedStateId().Returns(CreateStateId(5));
        _snapshotRepository.TryAdd(Arg.Any<Snapshot>(), SnapshotTier.InMemoryBase).Returns(true);

        ResourcePool realResourcePool = new(_config);
        using SemaphoreSlim persistJobDone = new(0);

        await using FlatDbManager manager = CreateManager();
        _persistenceManager.When(x => x.AddToPersistence(Arg.Any<StateId>()))
            .Do(_ => persistJobDone.Release());

        AddSnapshotAt(manager, realResourcePool, 11);
        Assert.That(await persistJobDone.WaitAsync(TimeSpan.FromSeconds(10)), Is.True);

        using (manager.GatherReadOnlySnapshotBundle(gatherStateId)) { }

        AddSnapshotAt(manager, realResourcePool, 12);
        Assert.That(await persistJobDone.WaitAsync(TimeSpan.FromSeconds(10)), Is.True);

        _persistenceManager.ClearReceivedCalls();
        using (manager.GatherReadOnlySnapshotBundle(gatherStateId)) { }
        _persistenceManager.DidNotReceive().LeaseReader();
    }

    [Test]
    public async Task PersistJob_WithPersistedStateAdvance_ClearsReadBundleCache()
    {
        StateId gatherStateId = CreateStateId(10);
        SetUpGather(gatherStateId);
        _persistenceManager.GetCurrentPersistedStateId().Returns(CreateStateId(5));
        _snapshotRepository.TryAdd(Arg.Any<Snapshot>(), SnapshotTier.InMemoryBase).Returns(true);

        ResourcePool realResourcePool = new(_config);
        using SemaphoreSlim persistJobDone = new(0);

        await using FlatDbManager manager = CreateManager();
        _persistenceManager.When(x => x.AddToPersistence(Arg.Any<StateId>()))
            .Do(_ => persistJobDone.Release());

        AddSnapshotAt(manager, realResourcePool, 11);
        Assert.That(await persistJobDone.WaitAsync(TimeSpan.FromSeconds(10)), Is.True);

        using (manager.GatherReadOnlySnapshotBundle(gatherStateId)) { }

        _persistenceManager.GetCurrentPersistedStateId().Returns(CreateStateId(12));
        AddSnapshotAt(manager, realResourcePool, 13);
        Assert.That(await persistJobDone.WaitAsync(TimeSpan.FromSeconds(10)), Is.True);

        _persistenceManager.ClearReceivedCalls();
        using (manager.GatherReadOnlySnapshotBundle(gatherStateId)) { }
        _persistenceManager.Received(1).LeaseReader();
    }

    private void SetUpGather(StateId stateId)
    {
        IPersistence.IPersistenceReader mockReader = Substitute.For<IPersistence.IPersistenceReader>();
        mockReader.CurrentState.Returns(stateId);
        _persistenceManager.LeaseReader().Returns(mockReader);
        _snapshotRepository.AssembleSnapshots(stateId, stateId, Arg.Any<int>())
            .Returns(_ => new AssembledSnapshotResult(new SnapshotPooledList(0), PersistedSnapshotList.Empty()));
    }

    private void AddSnapshotAt(FlatDbManager manager, ResourcePool resourcePool, ulong blockNumber)
    {
        Snapshot snapshot = resourcePool.CreateSnapshot(CreateStateId(blockNumber - 1), CreateStateId(blockNumber), ResourcePool.Usage.MainBlockProcessing);
        TransientResource transientResource = resourcePool.GetCachedResource(ResourcePool.Usage.MainBlockProcessing);
        manager.AddSnapshot(snapshot, transientResource);
    }

    [Test]
    public async Task AddSnapshot_DuplicateSnapshot_DisposesSnapshotAndReturnsResource()
    {
        StateId persistedStateId = CreateStateId(5);
        _persistenceManager.GetCurrentPersistedStateId().Returns(persistedStateId);
        _snapshotRepository.TryAdd(Arg.Any<Snapshot>(), SnapshotTier.InMemoryBase).Returns(false);

        ResourcePool realResourcePool = new(_config);
        StateId snapshotFrom = CreateStateId(10);
        StateId snapshotTo = CreateStateId(11);
        Snapshot snapshot = realResourcePool.CreateSnapshot(snapshotFrom, snapshotTo, ResourcePool.Usage.MainBlockProcessing);
        TransientResource transientResource = realResourcePool.GetCachedResource(ResourcePool.Usage.MainBlockProcessing);

        await using FlatDbManager manager = CreateManager();
        manager.AddSnapshot(snapshot, transientResource);

        _resourcePool.Received(1).ReturnCachedResource(ResourcePool.Usage.MainBlockProcessing, transientResource);
        _snapshotRepository.DidNotReceive().SetLastCommittedStateId(Arg.Any<StateId>());
    }
}
