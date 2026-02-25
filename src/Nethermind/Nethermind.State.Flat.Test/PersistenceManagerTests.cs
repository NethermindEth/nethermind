// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Flat.PersistedSnapshots;
using Nethermind.State.Flat.Storage;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test;

[TestFixture]
public class PersistenceManagerTests
{
    private PersistenceManager _persistenceManager = null!;
    private FlatDbConfig _config = null!;
    private TestFinalizedStateProvider _finalizedStateProvider = null!;
    private SnapshotRepository _snapshotRepository = null!;
    private IPersistence _persistence = null!;
    private IPersistedSnapshotCompactor _persistedSnapshotCompactor = null!;
    private IPersistedSnapshotRepository _persistedSnapshotRepository = null!;
    private ResourcePool _resourcePool = null!;
    private StateId Block0 = new StateId(0, Keccak.EmptyTreeHash);
    private MemoryArenaManager _memArena = null!;

    [SetUp]
    public void SetUp()
    {
        _config = new FlatDbConfig
        {
            CompactSize = 16,
            MinReorgDepth = 64,
            MaxInMemoryReorgDepth = 256,
            LongFinalityReorgDepth = 90000
        };

        _resourcePool = new ResourcePool(_config);
        _finalizedStateProvider = new TestFinalizedStateProvider();
        _snapshotRepository = new SnapshotRepository(NullPersistedSnapshotRepository.Instance, LimboLogs.Instance);
        _persistence = Substitute.For<IPersistence>();

        IPersistence.IPersistenceReader persistenceReader = Substitute.For<IPersistence.IPersistenceReader>();
        persistenceReader.CurrentState.Returns(Block0);
        _persistence.CreateReader().Returns(persistenceReader);

        _persistedSnapshotCompactor = Substitute.For<IPersistedSnapshotCompactor>();
        _persistedSnapshotRepository = Substitute.For<IPersistedSnapshotRepository>();
        _memArena = new MemoryArenaManager();

        _persistenceManager = new PersistenceManager(
            _config,
            _finalizedStateProvider,
            _persistence,
            _snapshotRepository,
            LimboLogs.Instance,
            _persistedSnapshotCompactor,
            _persistedSnapshotRepository);
    }

    [TearDown]
    public async Task TearDown()
    {
        await _persistenceManager.DisposeAsync();
        _memArena.Dispose();
        _persistedSnapshotRepository.Dispose();
    }

    private StateId CreateStateId(long blockNumber, byte rootByte = 0)
    {
        byte[] bytes = new byte[32];
        bytes[0] = rootByte;
        return new StateId(blockNumber, new ValueHash256(bytes));
    }

    private Snapshot CreateSnapshot(StateId from, StateId to, bool compacted = false)
    {
        Snapshot snapshot = _resourcePool.CreateSnapshot(from, to, ResourcePool.Usage.ReadOnlyProcessingEnv);
        snapshot.Content.Accounts[TestItem.AddressA] = new Account(1, 100);

        if (compacted)
        {
            _snapshotRepository.TryAddCompactedSnapshot(snapshot);
        }
        else
        {
            _snapshotRepository.TryAddSnapshot(snapshot);
        }

        // AddStateId is needed for GetStatesAtBlockNumber to work
        _snapshotRepository.AddStateId(to);

        return snapshot;
    }

    private Snapshot CreateSnapshotWithSelfDestruct(StateId from, StateId to)
    {
        Snapshot snapshot = _resourcePool.CreateSnapshot(from, to, ResourcePool.Usage.ReadOnlyProcessingEnv);
        snapshot.Content.SelfDestructedStorageAddresses[TestItem.AddressA] = false; // false = should be processed
        return snapshot;
    }

    [Test]
    public void DetermineSnapshotAction_InsufficientInMemoryDepth_ReturnsNull()
    {
        // Setup: persisted at Block0 (0), latest at 60, after persist would be < 64 minimum
        StateId persisted = Block0;
        StateId latest = CreateStateId(60);
        _finalizedStateProvider.SetFinalizedBlockNumber(100);

        (PersistedSnapshot? persistedToPersist, Snapshot? toPersist, long? toConvert) = _persistenceManager.DetermineSnapshotAction(latest);

        Assert.That(persistedToPersist, Is.Null);
        Assert.That(toPersist, Is.Null);
        Assert.That(toConvert, Is.Null);
    }

    [TestCase(true, TestName = "DetermineSnapshotAction_SufficientDepthAndFinalized_ReturnsCompactedSnapshot")]
    [TestCase(false, TestName = "DetermineSnapshotAction_SufficientDepthAndFinalized_FallsBackToUncompacted")]
    public void DetermineSnapshotAction_SufficientDepthAndFinalized(bool useCompacted)
    {
        // Setup: persisted at Block0, latest at 100, finalized at 100
        StateId persisted = Block0;
        StateId latest = CreateStateId(100);

        // Vary target block and compaction based on parameter
        int targetBlock = useCompacted ? 16 : 1; // compacted uses 16, fallback uses 1
        StateId target = CreateStateId(targetBlock);

        _finalizedStateProvider.SetFinalizedBlockNumber(100);
        _finalizedStateProvider.SetFinalizedStateRootAt(targetBlock, new Hash256(target.StateRoot.Bytes));

        // Create snapshot (compacted or not based on parameter)
        using Snapshot expectedSnapshot = CreateSnapshot(persisted, target, compacted: useCompacted);

        (PersistedSnapshot? persistedToPersist, Snapshot? toPersist, long? toConvert) = _persistenceManager.DetermineSnapshotAction(latest);

        Assert.That(persistedToPersist, Is.Null);
        Assert.That(toPersist, Is.Not.Null);
        Assert.That(toConvert, Is.Null);
        Assert.That(toPersist!.From, Is.EqualTo(persisted));
        Assert.That(toPersist.To, Is.EqualTo(target));

        toPersist.Dispose();
    }

    [Test]
    public void DetermineSnapshotAction_UnfinalizedButBelowForceLimit_ReturnsNull()
    {
        // Setup: persisted at Block0, latest at 150, finalized at 10 (way behind)
        // After persist would be at 16, which is > finalized
        // But in-memory depth is 150 (< 256 forced boundary)
        StateId persisted = Block0;
        StateId latest = CreateStateId(150);
        _finalizedStateProvider.SetFinalizedBlockNumber(10);

        (PersistedSnapshot? persistedToPersist, Snapshot? toPersist, long? toConvert) = _persistenceManager.DetermineSnapshotAction(latest);

        Assert.That(persistedToPersist, Is.Null);
        Assert.That(toPersist, Is.Null);
        Assert.That(toConvert, Is.Null);
    }

    [Test]
    public void DetermineSnapshotAction_UnfinalizedAndAboveForceLimit_ReturnsToConvert()
    {
        // Setup: persisted at Block0, latest at 300, finalized at 10
        // In-memory depth is ~301 (> 256 forced boundary)
        // Now returns ToConvert instead of force-persisting
        StateId persisted = Block0;
        StateId latest = CreateStateId(300);
        StateId target = CreateStateId(1);

        _finalizedStateProvider.SetFinalizedBlockNumber(10);

        // Create non-compacted snapshot chain from persisted state
        using Snapshot expectedSnapshot = CreateSnapshot(persisted, target, compacted: false);

        (PersistedSnapshot? persistedToPersist, Snapshot? toPersist, long? toConvert) = _persistenceManager.DetermineSnapshotAction(latest);

        Assert.That(persistedToPersist, Is.Null);
        Assert.That(toPersist, Is.Null);
        Assert.That(toConvert, Is.Not.Null);
    }

    [Test]
    public void DetermineSnapshotAction_NoSnapshotAvailable_ReturnsNull()
    {
        // Setup: sufficient depth but no snapshots in repository
        StateId persisted = Block0;
        StateId latest = CreateStateId(100);
        _finalizedStateProvider.SetFinalizedBlockNumber(100);
        _finalizedStateProvider.SetFinalizedStateRootAt(16, new Hash256(CreateStateId(16).StateRoot.Bytes));

        // Don't create any snapshots

        (PersistedSnapshot? persistedToPersist, Snapshot? toPersist, _) = _persistenceManager.DetermineSnapshotAction(latest);

        Assert.That(persistedToPersist, Is.Null);
        Assert.That(toPersist, Is.Null);
    }

    [Test]
    public void DetermineSnapshotAction_FinalizedNoInMemory_FallsBackToPersistedSnapshot()
    {
        // Setup: persisted at Block0, latest at 100, finalized at 100
        StateId latest = CreateStateId(100);
        _finalizedStateProvider.SetFinalizedBlockNumber(100);
        _finalizedStateProvider.SetFinalizedStateRootAt(16, new Hash256(CreateStateId(16).StateRoot.Bytes));

        // Don't create any in-memory snapshots — configure persisted snapshot fallback
        StateId target = CreateStateId(16);
        SnapshotLocation emptyLoc = _memArena.Allocate([]);
        ArenaReservation emptyRes = _memArena.Open(emptyLoc);
        PersistedSnapshot persisted = new PersistedSnapshot(1, Block0, target, PersistedSnapshotType.Full, emptyRes);
        _persistedSnapshotRepository.TryLeasePersistableCompactedSnapshotTo(target, out Arg.Any<PersistedSnapshot?>())
            .Returns(x => { x[1] = persisted; return true; });

        (PersistedSnapshot? persistedToPersist, Snapshot? toPersist, long? toConvert) = _persistenceManager.DetermineSnapshotAction(latest);

        Assert.That(persistedToPersist, Is.Not.Null);
        Assert.That(toPersist, Is.Null);
        Assert.That(toConvert, Is.Null);

        persistedToPersist!.Dispose();
    }

    [Test]
    public void DetermineSnapshotAction_SnapshotWithWrongFromState_ReturnsNull()
    {
        // Setup: snapshot exists but doesn't start from current persisted state
        StateId persisted = Block0;
        StateId latest = CreateStateId(100);
        StateId wrongFrom = CreateStateId(5);
        StateId target = CreateStateId(16);
        _finalizedStateProvider.SetFinalizedBlockNumber(100);
        _finalizedStateProvider.SetFinalizedStateRootAt(16, new Hash256(target.StateRoot.Bytes));

        // Create snapshot with wrong "from" state
        using Snapshot wrongSnapshot = CreateSnapshot(wrongFrom, target, compacted: true);

        (PersistedSnapshot? persistedToPersist, Snapshot? toPersist, _) = _persistenceManager.DetermineSnapshotAction(latest);

        Assert.That(persistedToPersist, Is.Null);
        Assert.That(toPersist, Is.Null);
    }

    [Test]
    public void DetermineSnapshotAction_MultipleStatesAtBlock_SelectsCorrectOne()
    {
        // Setup: multiple state roots at same block number (reorg scenario)
        StateId persisted = Block0;
        StateId latest = CreateStateId(100);
        StateId target1 = CreateStateId(16, rootByte: 1);
        StateId target2 = CreateStateId(16, rootByte: 2); // Different root
        _finalizedStateProvider.SetFinalizedBlockNumber(100);
        _finalizedStateProvider.SetFinalizedStateRootAt(16, new Hash256(target2.StateRoot.Bytes)); // target2 is finalized

        // Create both snapshots
        using Snapshot snapshot1 = CreateSnapshot(persisted, target1, compacted: true);
        using Snapshot snapshot2 = CreateSnapshot(persisted, target2, compacted: true);

        (PersistedSnapshot? persistedToPersist, Snapshot? toPersist, _) = _persistenceManager.DetermineSnapshotAction(latest);

        Assert.That(persistedToPersist, Is.Null);
        Assert.That(toPersist, Is.Not.Null);
        Assert.That(toPersist!.To.StateRoot.Bytes.ToArray(), Is.EqualTo(target2.StateRoot.Bytes.ToArray()));

        toPersist.Dispose();
    }

    [Test]
    public void DetermineSnapshotAction_ExactlyAtMinimumBoundary_ReturnsNull()
    {
        // Setup: persisted at Block0 (0), latest at 79
        // After persist would be at 15, leaving depth of 64 (exactly at minimum boundary)
        StateId persisted = Block0;
        StateId latest = CreateStateId(79);
        _finalizedStateProvider.SetFinalizedBlockNumber(100);

        (PersistedSnapshot? persistedToPersist, Snapshot? toPersist, _) = _persistenceManager.DetermineSnapshotAction(latest);

        Assert.That(persistedToPersist, Is.Null);
        Assert.That(toPersist, Is.Null);
    }

    [Test]
    public void DetermineSnapshotAction_OneAboveMinimumBoundary_ReturnsSnapshot()
    {
        // Setup: persisted at Block0 (0), latest at 80
        // After persist would be at 15, leaving depth of 65 (one above minimum boundary)
        StateId persisted = Block0;
        StateId latest = CreateStateId(80);
        StateId target = CreateStateId(16);
        _finalizedStateProvider.SetFinalizedBlockNumber(100);
        _finalizedStateProvider.SetFinalizedStateRootAt(16, new Hash256(target.StateRoot.Bytes));

        using Snapshot expectedSnapshot = CreateSnapshot(persisted, target, compacted: true);

        (PersistedSnapshot? persistedToPersist, Snapshot? toPersist, _) = _persistenceManager.DetermineSnapshotAction(latest);

        Assert.That(persistedToPersist, Is.Null);
        Assert.That(toPersist, Is.Not.Null);

        toPersist!.Dispose();
    }

    [Test]
    public void PersistSnapshot_WithAccountsStorageAndTrieNodes_WritesToBatch()
    {
        // Arrange
        StateId from = Block0;
        StateId to = CreateStateId(16);
        using Snapshot snapshot = _resourcePool.CreateSnapshot(from, to, ResourcePool.Usage.ReadOnlyProcessingEnv);

        // Add accounts
        snapshot.Content.Accounts[TestItem.AddressA] = new Account(1, 100);
        snapshot.Content.Accounts[TestItem.AddressB] = new Account(2, 200);

        // Add storage
        snapshot.Content.Storages[(TestItem.AddressA, (UInt256)1)] = SlotValue.FromSpanWithoutLeadingZero([42]);
        snapshot.Content.Storages[(TestItem.AddressA, (UInt256)2)] = SlotValue.FromSpanWithoutLeadingZero([99]);

        // Add trie nodes
        TreePath path = TreePath.Empty;
        TrieNode node = new TrieNode(NodeType.Leaf, Keccak.Zero);
        snapshot.Content.StateNodes[path] = node;

        IPersistence.IWriteBatch writeBatch = Substitute.For<IPersistence.IWriteBatch>();
        _persistence.CreateWriteBatch(from, to).Returns(writeBatch);

        // Act
        _persistenceManager.PersistSnapshot(snapshot);

        // Assert
        writeBatch.Received().SetAccount(TestItem.AddressA, Arg.Any<Account?>());
        writeBatch.Received().SetAccount(TestItem.AddressB, Arg.Any<Account?>());
        writeBatch.Received().SetStorage(TestItem.AddressA, (UInt256)1, Arg.Any<SlotValue?>());
        writeBatch.Received().SetStorage(TestItem.AddressA, (UInt256)2, Arg.Any<SlotValue?>());
        writeBatch.Received().SetStateTrieNode(Arg.Any<TreePath>(), Arg.Any<TrieNode>());
        Assert.That(node.IsPersisted, Is.True);
    }

    [Test]
    public void PersistSnapshot_WithSelfDestructedAddresses_CallsSelfDestruct()
    {
        // Arrange
        StateId from = Block0;
        StateId to = CreateStateId(16);
        using Snapshot snapshot = CreateSnapshotWithSelfDestruct(from, to);

        IPersistence.IWriteBatch writeBatch = Substitute.For<IPersistence.IWriteBatch>();
        _persistence.CreateWriteBatch(from, to).Returns(writeBatch);

        // Act
        _persistenceManager.PersistSnapshot(snapshot);

        // Assert
        writeBatch.Received().SelfDestruct(TestItem.AddressA);
    }

    [Test]
    public void PersistSnapshot_EmptySnapshot_CreatesWriteBatch()
    {
        // Arrange
        StateId from = Block0;
        StateId to = CreateStateId(16);
        using Snapshot snapshot = _resourcePool.CreateSnapshot(from, to, ResourcePool.Usage.ReadOnlyProcessingEnv);

        IPersistence.IWriteBatch writeBatch = Substitute.For<IPersistence.IWriteBatch>();
        _persistence.CreateWriteBatch(from, to).Returns(writeBatch);

        // Act
        _persistenceManager.PersistSnapshot(snapshot);

        // Assert
        _persistence.Received(1).CreateWriteBatch(from, to);
    }

    [Test]
    public void AddToPersistence_WithAvailableSnapshot_PersistsAndUpdatesState()
    {
        // Arrange
        StateId from = Block0;
        StateId to = CreateStateId(16);
        StateId latest = CreateStateId(100);

        // Create a snapshot that should be persisted
        using Snapshot snapshot = CreateSnapshot(from, to, compacted: true);

        _finalizedStateProvider.SetFinalizedBlockNumber(100);
        _finalizedStateProvider.SetFinalizedStateRootAt(16, new Hash256(to.StateRoot.Bytes));

        IPersistence.IWriteBatch writeBatch = Substitute.For<IPersistence.IWriteBatch>();
        _persistence.CreateWriteBatch(Arg.Any<StateId>(), Arg.Any<StateId>()).Returns(writeBatch);

        // Act
        _persistenceManager.AddToPersistence(latest);

        // Assert
        // Verify write batch was created (persistence happened)
        _persistence.Received().CreateWriteBatch(from, to);

        // Verify current persisted state was updated
        Assert.That(_persistenceManager.GetCurrentPersistedStateId(), Is.EqualTo(to));
    }

    [Test]
    public void FlushToPersistence_NoSnapshots_ReturnsCurrentPersistedState()
    {
        // Arrange - no snapshots added
        StateId persisted = Block0;

        // Act
        StateId result = _persistenceManager.FlushToPersistence();

        // Assert
        Assert.That(result, Is.EqualTo(persisted));
    }

    [Test]
    public void FlushToPersistence_WithFinalizedSnapshots_PersistsFinalizedFirst()
    {
        // Arrange
        StateId state16 = CreateStateId(16);
        StateId state32 = CreateStateId(32);

        _finalizedStateProvider.SetFinalizedBlockNumber(100);
        _finalizedStateProvider.SetFinalizedStateRootAt(16, new Hash256(state16.StateRoot.Bytes));
        _finalizedStateProvider.SetFinalizedStateRootAt(32, new Hash256(state32.StateRoot.Bytes));

        using Snapshot snapshot1 = CreateSnapshot(Block0, state16, compacted: true);
        using Snapshot snapshot2 = CreateSnapshot(state16, state32, compacted: true);

        IPersistence.IWriteBatch writeBatch = Substitute.For<IPersistence.IWriteBatch>();
        _persistence.CreateWriteBatch(Arg.Any<StateId>(), Arg.Any<StateId>()).Returns(writeBatch);

        // Act
        StateId result = _persistenceManager.FlushToPersistence();

        // Assert
        Assert.That(result, Is.EqualTo(state32));
        _persistence.Received().CreateWriteBatch(Block0, state16);
        _persistence.Received().CreateWriteBatch(state16, state32);
    }

    [Test]
    public void FlushToPersistence_WithUnfinalizedSnapshots_FallsBackToFirstAvailable()
    {
        // Arrange - no finalization info available
        StateId state16 = CreateStateId(16);
        _finalizedStateProvider.SetFinalizedBlockNumber(0); // Nothing finalized

        using Snapshot snapshot = CreateSnapshot(Block0, state16, compacted: true);

        IPersistence.IWriteBatch writeBatch = Substitute.For<IPersistence.IWriteBatch>();
        _persistence.CreateWriteBatch(Arg.Any<StateId>(), Arg.Any<StateId>()).Returns(writeBatch);

        // Act
        StateId result = _persistenceManager.FlushToPersistence();

        // Assert
        Assert.That(result, Is.EqualTo(state16));
        _persistence.Received().CreateWriteBatch(Block0, state16);
    }

    [Test]
    public void FlushToPersistence_PrefersFinalizedOverUnfinalized()
    {
        // Arrange - two snapshots at same block, one finalized
        StateId finalizedState = CreateStateId(16, rootByte: 1);
        StateId unfinalizedState = CreateStateId(16, rootByte: 2);

        _finalizedStateProvider.SetFinalizedBlockNumber(100);
        _finalizedStateProvider.SetFinalizedStateRootAt(16, new Hash256(finalizedState.StateRoot.Bytes));

        // Create both snapshots
        using Snapshot finalizedSnapshot = CreateSnapshot(Block0, finalizedState, compacted: true);
        using Snapshot unfinalizedSnapshot = CreateSnapshot(Block0, unfinalizedState, compacted: true);

        IPersistence.IWriteBatch writeBatch = Substitute.For<IPersistence.IWriteBatch>();
        _persistence.CreateWriteBatch(Arg.Any<StateId>(), Arg.Any<StateId>()).Returns(writeBatch);

        // Act
        StateId result = _persistenceManager.FlushToPersistence();

        // Assert - should persist finalized state
        Assert.That(result.StateRoot.Bytes.ToArray(), Is.EqualTo(finalizedState.StateRoot.Bytes.ToArray()));
    }

    [Test]
    public void FlushToPersistence_PersistsMultipleSnapshots_InOrder()
    {
        // Arrange
        StateId state1 = CreateStateId(1);
        StateId state2 = CreateStateId(2);
        StateId state3 = CreateStateId(3);

        // No finalization - will use first available
        _finalizedStateProvider.SetFinalizedBlockNumber(0);

        using Snapshot snapshot1 = CreateSnapshot(Block0, state1, compacted: false);
        using Snapshot snapshot2 = CreateSnapshot(state1, state2, compacted: false);
        using Snapshot snapshot3 = CreateSnapshot(state2, state3, compacted: false);

        IPersistence.IWriteBatch writeBatch = Substitute.For<IPersistence.IWriteBatch>();
        _persistence.CreateWriteBatch(Arg.Any<StateId>(), Arg.Any<StateId>()).Returns(writeBatch);

        // Act
        StateId result = _persistenceManager.FlushToPersistence();

        // Assert
        Assert.That(result, Is.EqualTo(state3));
        Received.InOrder(() =>
        {
            _persistence.CreateWriteBatch(Block0, state1);
            _persistence.CreateWriteBatch(state1, state2);
            _persistence.CreateWriteBatch(state2, state3);
        });
    }

    private class TestFinalizedStateProvider : IFinalizedStateProvider
    {
        private long _finalizedBlockNumber;
        private readonly Dictionary<long, Hash256> _finalizedStateRoots = new();

        public long FinalizedBlockNumber => _finalizedBlockNumber;

        public void SetFinalizedBlockNumber(long blockNumber) => _finalizedBlockNumber = blockNumber;

        public void SetFinalizedStateRootAt(long blockNumber, Hash256 stateRoot) => _finalizedStateRoots[blockNumber] = stateRoot;

        public Hash256? GetFinalizedStateRootAt(long blockNumber) =>
            _finalizedStateRoots.TryGetValue(blockNumber, out Hash256? root) ? root : null;
    }

}
