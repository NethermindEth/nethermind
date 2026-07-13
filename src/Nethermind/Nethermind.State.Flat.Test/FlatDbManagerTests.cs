// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Flat.History;
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

    private const long HistoryBarrier = 100;
    private static readonly Address HistoryAddr = new("0x0000000000000000000000000000000000000abc");
    private static readonly UInt256 HistorySlot = 7;

    private SnapshotableMemColumnsDb<FlatDbColumns> _historyDb = null!;
    private SnapshotableMemColumnsDb<FlatHistoryColumns> _historyColumns = null!;
    private HistoryReader _historyReader = null!;
    private HistoryStore _accountStore = null!;
    private HistoryStore _storageStore = null!;

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

        _historyDb = new SnapshotableMemColumnsDb<FlatDbColumns>();
        _historyColumns = new SnapshotableMemColumnsDb<FlatHistoryColumns>();
        _historyReader = new HistoryReader(_historyDb, _historyColumns, LimboLogs.Instance);
        _accountStore = new HistoryStore(
            _historyColumns.GetColumnDb(FlatHistoryColumns.AccountHistory),
            _historyColumns.GetColumnDb(FlatHistoryColumns.AccountChangeSets));
        _storageStore = new HistoryStore(
            _historyColumns.GetColumnDb(FlatHistoryColumns.StorageHistory),
            _historyColumns.GetColumnDb(FlatHistoryColumns.StorageChangeSets));
    }

    [TearDown]
    public void TearDown()
    {
        _persistedSnapshotLoader.Dispose();
        _cts.Cancel();
        _cts.Dispose();
        _historyDb.Dispose();
        _historyColumns.Dispose();
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

    // Account: set @5, overwritten @20, deleted @30. Slot: 0xAA @5, 0xBBCC @20, cleared @30.
    [TestCase(3ul, 0L, null)]
    [TestCase(10ul, 5L, "aa")]
    [TestCase(19ul, 5L, "aa")]
    [TestCase(20ul, 20L, "bbcc")]
    [TestCase(29ul, 20L, "bbcc")]
    [TestCase(30ul, 0L, null)]
    [TestCase(35ul, 0L, null)]
    public async Task GatherReadOnlySnapshotBundle_below_barrier_reads_history(ulong block, long expectedNonce, string? expectedSlotHex)
    {
        _persistenceManager.GetCurrentPersistedStateId().Returns(CreateStateId(HistoryBarrier));
        RecordHistoryWindow();
        StateId historicalBlock = CreateStateId(block, (byte)block);

        await using FlatDbManager inner = CreateManager();
        HistoricalFlatDbManager manager = WrapHistory(inner);
        using ReadOnlySnapshotBundle bundle = manager.GatherReadOnlySnapshotBundle(historicalBlock);

        Account? account = bundle.GetAccount(HistoryAddr);
        byte[]? slot = bundle.GetSlot(HistoryAddr, HistorySlot, bundle.DetermineSelfDestructSnapshotIdx(HistoryAddr));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(bundle, Is.Not.Null);

            if (expectedNonce == 0)
            {
                Assert.That(account, Is.Null);
            }
            else
            {
                Assert.That(account, Is.Not.Null);
                Assert.That(account!.Nonce, Is.EqualTo((ulong)expectedNonce));
                Assert.That(account.Balance, Is.EqualTo((UInt256)(expectedNonce * 100)));
            }

            if (expectedSlotHex is null)
            {
                Assert.That(slot, Is.Null.Or.EqualTo(new byte[] { 0 }));
            }
            else
            {
                Assert.That(slot!.WithoutLeadingZeros().ToArray(), Is.EqualTo(Convert.FromHexString(expectedSlotHex)));
            }
        }
    }

    [TestCase(true, true)]
    [TestCase(false, false)]
    public async Task HasStateForBlock_below_barrier_follows_history_flag(bool historyEnabled, bool expected)
    {
        _persistenceManager.GetCurrentPersistedStateId().Returns(CreateStateId(HistoryBarrier));
        StateId historicalBlock = CreateStateId(10, rootByte: 10);
        _snapshotRepository.HasState(historicalBlock).Returns(false);
        MarkBlocksAvailable(0, (ulong)HistoryBarrier);

        await using FlatDbManager inner = CreateManager();
        IFlatDbManager manager = historyEnabled ? WrapHistory(inner) : inner;
        Assert.That(manager.HasStateForBlock(historicalBlock), Is.EqualTo(expected));
    }

    private HistoricalFlatDbManager WrapHistory(FlatDbManager inner) => new(
        inner,
        _persistenceManager,
        _historyReader,
        _trieNodeCache,
        _resourcePool,
        enableDetailedMetrics: false);

    private void RecordHistoryWindow()
    {
        RecordAccount(5, new Account(5, 500));
        RecordAccount(20, new Account(20, 2000));
        RecordAccount(30, account: null);

        RecordStorage(5, [0xAA]);
        RecordStorage(20, [0xBB, 0xCC]);
        RecordStorage(30, ReadOnlySpan<byte>.Empty);

        // Every processed block below the barrier carries an availability marker, as CaptureBlock writes one per
        // captured block regardless of whether that block changed any account or slot.
        MarkBlocksAvailable(0, (ulong)HistoryBarrier);
    }

    private void MarkBlocksAvailable(ulong fromInclusive, ulong toExclusive)
    {
        using IColumnsWriteBatch<FlatHistoryColumns> batch = _historyColumns.StartWriteBatch();
        IWriteBatch available = batch.GetColumnBatch(FlatHistoryColumns.AvailableBlocks);
        Span<byte> key = stackalloc byte[sizeof(ulong)];
        for (ulong block = fromInclusive; block < toExclusive; block++)
        {
            System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(key, block);
            available.Set(key, Array.Empty<byte>());
        }
    }

    private void RecordAccount(ulong block, Account? account)
    {
        ReadOnlySpan<byte> flatKey = BaseFlatPersistence.EncodeAccountKeyHashed(
            stackalloc byte[BaseFlatPersistence.AccountKeyLength], HistoryAddr.ToAccountPath);

        using IColumnsWriteBatch<FlatHistoryColumns> batch = _historyColumns.StartWriteBatch();
        IWriteBatch history = batch.GetColumnBatch(FlatHistoryColumns.AccountHistory);
        IWriteBatch changeMarkers = batch.GetColumnBatch(FlatHistoryColumns.AccountChangeSets);

        if (account is null)
        {
            _accountStore.RecordChange(block, flatKey, ReadOnlySpan<byte>.Empty, history, changeMarkers);
            return;
        }

        using ArrayPoolSpan<byte> rlp = AccountDecoder.Slim.EncodeToArrayPoolSpan(account);
        _accountStore.RecordChange(block, flatKey, rlp, history, changeMarkers);
    }

    private void RecordStorage(ulong block, ReadOnlySpan<byte> rawValue)
    {
        ValueHash256 slotHash = ValueKeccak.Zero;
        StorageTree.ComputeKeyWithLookup(HistorySlot, ref slotHash);
        ReadOnlySpan<byte> flatKey = BaseFlatPersistence.EncodeStorageKeyHashedWithShortPrefix(
            stackalloc byte[BaseFlatPersistence.StorageKeyLength], HistoryAddr.ToAccountPath, slotHash);

        Span<byte> value = stackalloc byte[BaseFlatPersistence.RlpSlotValueBufferSize];
        int written = rawValue.IsEmpty
            ? 0
            : BaseFlatPersistence.EncodeSlotValue(SlotValue.FromSpanWithoutLeadingZero(rawValue), rlpWrapSlots: true, value);

        using IColumnsWriteBatch<FlatHistoryColumns> batch = _historyColumns.StartWriteBatch();
        _storageStore.RecordChange(
            block, flatKey, value[..written],
            batch.GetColumnBatch(FlatHistoryColumns.StorageHistory),
            batch.GetColumnBatch(FlatHistoryColumns.StorageChangeSets));
    }
}
