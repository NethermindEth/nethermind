// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State.Flat.Persistence;
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
    private ResourcePool _resourcePool = null!;
    private StateId Block0 = new(0, Keccak.EmptyTreeHash);

    [SetUp]
    public void SetUp()
    {
        _config = new FlatDbConfig
        {
            CompactSize = 16,
            MinReorgDepth = 64,
            MaxReorgDepth = 256
        };

        _resourcePool = new ResourcePool(_config);
        _finalizedStateProvider = new TestFinalizedStateProvider();
        _snapshotRepository = new SnapshotRepository(LimboLogs.Instance);
        _persistence = Substitute.For<IPersistence>();

        IPersistence.IPersistenceReader persistenceReader = Substitute.For<IPersistence.IPersistenceReader>();
        persistenceReader.CurrentState.Returns(Block0);
        _persistence.CreateReader().Returns(persistenceReader);

        _persistenceManager = CreateManager();
    }

    private PersistenceManager CreateManager(int offset = 0) => new(
        _config,
        ScheduleHelper.CreateWithOffset(_config, offset),
        _finalizedStateProvider,
        _persistence,
        _snapshotRepository,
        _resourcePool,
        LimboLogs.Instance);

    [TearDown]
    public void TearDown()
    {
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

    #region Basic Behavior Tests

    [Test]
    public void DetermineSnapshotToPersist_InsufficientInMemoryDepth_ReturnsNull()
    {
        // Setup: persisted at Block0 (0), latest at 60, after persist would be < 64 minimum
        StateId persisted = Block0;
        StateId latest = CreateStateId(60);
        _finalizedStateProvider.SetFinalizedBlockNumber(100);

        Snapshot? result = _persistenceManager.DetermineSnapshotToPersist(latest);

        Assert.That(result, Is.Null);
    }

    [TestCase(true, TestName = "DetermineSnapshotToPersist_SufficientDepthAndFinalized_ReturnsCompactedSnapshot")]
    [TestCase(false, TestName = "DetermineSnapshotToPersist_SufficientDepthAndFinalized_FallsBackToUncompacted")]
    public void DetermineSnapshotToPersist_SufficientDepthAndFinalized(bool useCompacted)
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

        Snapshot? result = _persistenceManager.DetermineSnapshotToPersist(latest);

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.From, Is.EqualTo(persisted));
        Assert.That(result.To, Is.EqualTo(target));

        result.Dispose();
    }

    #endregion

    #region Unfinalized State Tests

    [Test]
    public void DetermineSnapshotToPersist_UnfinalizedButBelowForceLimit_ReturnsNull()
    {
        // Setup: persisted at Block0, latest at 150, finalized at 10 (way behind)
        // After persist would be at 16, which is > finalized
        // But in-memory depth is 150 (< 256 forced boundary)
        StateId persisted = Block0;
        StateId latest = CreateStateId(150);
        _finalizedStateProvider.SetFinalizedBlockNumber(10);

        Snapshot? result = _persistenceManager.DetermineSnapshotToPersist(latest);

        Assert.That(result, Is.Null);
    }

    [TestCase(true, TestName = "DetermineSnapshotToPersist_UnfinalizedAndAboveForceLimit_ForcePersistsCompacted")]
    [TestCase(false, TestName = "DetermineSnapshotToPersist_UnfinalizedAndAboveForceLimit_FallsBackToUncompacted")]
    public void DetermineSnapshotToPersist_UnfinalizedAndAboveForceLimit(bool useCompacted)
    {
        // Setup: persisted at Block0, latest at 300, finalized at 10
        // In-memory depth is ~301 (> 256 forced boundary)
        StateId persisted = Block0;
        StateId latest = CreateStateId(300);

        // Vary target block and compaction based on parameter
        int targetBlock = useCompacted ? 16 : 1; // compacted uses 16, fallback uses 1
        StateId target = CreateStateId(targetBlock);

        _finalizedStateProvider.SetFinalizedBlockNumber(10);

        // Create snapshot (compacted or not based on parameter)
        using Snapshot expectedSnapshot = CreateSnapshot(persisted, target, compacted: useCompacted);

        Snapshot? result = _persistenceManager.DetermineSnapshotToPersist(latest);

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.From, Is.EqualTo(persisted));
        Assert.That(result.To, Is.EqualTo(target));

        result.Dispose();
    }

    #endregion

    #region Edge Cases

    [Test]
    public void DetermineSnapshotToPersist_NoSnapshotAvailable_ReturnsNull()
    {
        // Setup: sufficient depth but no snapshots in repository
        StateId persisted = Block0;
        StateId latest = CreateStateId(100);
        _finalizedStateProvider.SetFinalizedBlockNumber(100);
        _finalizedStateProvider.SetFinalizedStateRootAt(16, new Hash256(CreateStateId(16).StateRoot.Bytes));

        // Don't create any snapshots

        Snapshot? result = _persistenceManager.DetermineSnapshotToPersist(latest);

        Assert.That(result, Is.Null);
    }

    [Test]
    public void DetermineSnapshotToPersist_SnapshotWithWrongFromState_ReturnsNull()
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

        Snapshot? result = _persistenceManager.DetermineSnapshotToPersist(latest);

        Assert.That(result, Is.Null);
    }

    [Test]
    public void DetermineSnapshotToPersist_MultipleStatesAtBlock_SelectsCorrectOne()
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

        Snapshot? result = _persistenceManager.DetermineSnapshotToPersist(latest);

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.To.StateRoot.Bytes.ToArray(), Is.EqualTo(target2.StateRoot.Bytes.ToArray())); // Should select finalized one

        result.Dispose();
    }

    [Test]
    public void DetermineSnapshotToPersist_ExactlyAtMinimumBoundary_ReturnsNull()
    {
        // Setup: persisted at Block0 (0), latest at 79
        // After persist would be at 15, leaving depth of 64 (exactly at minimum boundary)
        StateId persisted = Block0;
        StateId latest = CreateStateId(79);
        _finalizedStateProvider.SetFinalizedBlockNumber(100);

        Snapshot? result = _persistenceManager.DetermineSnapshotToPersist(latest);

        Assert.That(result, Is.Null);
    }

    [Test]
    public void DetermineSnapshotToPersist_OneAboveMinimumBoundary_ReturnsSnapshot()
    {
        // Setup: persisted at Block0 (0), latest at 80
        // After persist would be at 15, leaving depth of 65 (one above minimum boundary)
        StateId persisted = Block0;
        StateId latest = CreateStateId(80);
        StateId target = CreateStateId(16);
        _finalizedStateProvider.SetFinalizedBlockNumber(100);
        _finalizedStateProvider.SetFinalizedStateRootAt(16, new Hash256(target.StateRoot.Bytes));

        using Snapshot expectedSnapshot = CreateSnapshot(persisted, target, compacted: true);

        Snapshot? result = _persistenceManager.DetermineSnapshotToPersist(latest);

        Assert.That(result, Is.Not.Null);

        result!.Dispose();
    }

    #endregion

    #region PersistSnapshot Tests

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
        TrieNode node = new(NodeType.Leaf, Keccak.Zero);
        snapshot.Content.StateNodes[path] = node;

        FakeWriteBatch writeBatch = new();
        _persistence.CreateWriteBatch(from, to).Returns(writeBatch);

        // Act
        _persistenceManager.PersistSnapshot(snapshot);

        // Assert
        Assert.That(writeBatch.SetAccountCalls, Has.Some.Matches<(Address Addr, Account? Account)>(c => c.Addr == TestItem.AddressA));
        Assert.That(writeBatch.SetAccountCalls, Has.Some.Matches<(Address Addr, Account? Account)>(c => c.Addr == TestItem.AddressB));
        Assert.That(writeBatch.SetStorageCalls, Has.Some.Matches<(Address Addr, UInt256 Slot, SlotValue? Value)>(c => c.Addr == TestItem.AddressA && c.Slot == (UInt256)1));
        Assert.That(writeBatch.SetStorageCalls, Has.Some.Matches<(Address Addr, UInt256 Slot, SlotValue? Value)>(c => c.Addr == TestItem.AddressA && c.Slot == (UInt256)2));
        Assert.That(writeBatch.SetStateTrieNodeCalls, Is.Not.Empty);
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

    #endregion

    #region AddToPersistence Tests

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

    #endregion

    #region Offset Behavior

    [TestCase(3, 13)]
    [TestCase(5, 11)]
    [TestCase(0, 16)]
    public void DetermineSnapshotToPersist_WithOffset_FirstBoundaryShifted(int offset, int expectedTargetBlock)
    {
        // Fresh DB: currentPersistedState = Block0 (block 0).
        // With CompactSize=16 and offset=N, the next full compaction boundary is at block 16-N.
        PersistenceManager pm = CreateManager(offset);

        StateId target = CreateStateId(expectedTargetBlock);
        StateId latest = CreateStateId(200);
        _finalizedStateProvider.SetFinalizedBlockNumber(200);
        _finalizedStateProvider.SetFinalizedStateRootAt(expectedTargetBlock, new Hash256(target.StateRoot.Bytes));

        using Snapshot expected = CreateSnapshot(Block0, target, compacted: true);

        Snapshot? result = pm.DetermineSnapshotToPersist(latest);

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.To, Is.EqualTo(target));
        result.Dispose();
    }

    #endregion

    #region FlushToPersistence Tests

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

    #endregion

    #region Early Persist (reverse diff)

    [Test]
    public void DetermineSnapshotToPersist_EarlyPersist_IgnoresMinReorgDepthButKeepsFinalizationGate()
    {
        _config.EarlyPersist = true;
        PersistenceManager pm = CreateManager();

        // Depth 20 is far below MinReorgDepth (64); only the finalization gate should matter.
        StateId target = CreateStateId(16);
        StateId latest = CreateStateId(20);
        using Snapshot expected = CreateSnapshot(Block0, target, compacted: true);
        _finalizedStateProvider.SetFinalizedStateRootAt(16, new Hash256(target.StateRoot.Bytes));

        _finalizedStateProvider.SetFinalizedBlockNumber(10);
        Snapshot? whileUnfinalized = pm.DetermineSnapshotToPersist(latest);

        _finalizedStateProvider.SetFinalizedBlockNumber(18);
        Snapshot? whenFinalized = pm.DetermineSnapshotToPersist(latest);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(whileUnfinalized, Is.Null, "boundary above finalized must not persist");
            Assert.That(whenFinalized, Is.Not.Null, "finalized boundary should persist regardless of depth");
            Assert.That(whenFinalized?.To, Is.EqualTo(target));
        }

        whenFinalized?.Dispose();
    }

    [Test]
    public void PersistSnapshot_EarlyPersist_BuildsReverseDiffWithOldValuesAndNullMarkers()
    {
        _config.EarlyPersist = true;
        PersistenceManager pm = CreateManager();

        Account oldAccount = new(5, 500);
        SlotValue oldSlot = SlotValue.FromSpanWithoutLeadingZero([7]);
        byte[] oldNodeRlp = [1, 2, 3];
        byte[] oldStorageNodeRlp = [4, 5, 6];
        TreePath presentPath = TreePath.FromHexString("12");
        TreePath absentPath = TreePath.FromHexString("34");

        FakePersistenceReader oldState = new() { CurrentState = Block0 };
        oldState.Accounts[TestItem.AddressA] = oldAccount;
        oldState.Slots[(TestItem.AddressA, (UInt256)1)] = oldSlot;
        oldState.StateRlp[presentPath] = oldNodeRlp;
        oldState.StorageRlp[(TestItem.KeccakA, presentPath)] = oldStorageNodeRlp;
        _persistence.CreateReader().Returns(oldState);

        StateId to = CreateStateId(16);
        using Snapshot snapshot = _resourcePool.CreateSnapshot(Block0, to, ResourcePool.Usage.ReadOnlyProcessingEnv);
        snapshot.Content.Accounts[TestItem.AddressA] = new Account(1, 100);
        snapshot.Content.Accounts[TestItem.AddressB] = new Account(2, 200);
        snapshot.Content.Storages[(TestItem.AddressA, (UInt256)1)] = SlotValue.FromSpanWithoutLeadingZero([42]);
        snapshot.Content.Storages[(TestItem.AddressA, (UInt256)2)] = SlotValue.FromSpanWithoutLeadingZero([99]);
        snapshot.Content.StateNodes[presentPath] = new TrieNode(NodeType.Leaf, Keccak.Zero);
        snapshot.Content.StateNodes[absentPath] = new TrieNode(NodeType.Leaf, Keccak.Zero);
        snapshot.Content.StorageNodes[(TestItem.KeccakA, presentPath)] = new TrieNode(NodeType.Leaf, Keccak.Zero);

        FakeWriteBatch writeBatch = new();
        _persistence.CreateWriteBatch(Block0, to).Returns(writeBatch);

        pm.PersistSnapshot(snapshot);

        using SnapshotPooledList assembled = _snapshotRepository.AssembleHistoricalSnapshots(Block0, to, 1);
        Assert.That(assembled.Count, Is.EqualTo(1), "reverse diff should be registered");
        Snapshot reverseDiff = assembled[0];

        using (Assert.EnterMultipleScope())
        {
            Assert.That(reverseDiff.From, Is.EqualTo(to));
            Assert.That(reverseDiff.To, Is.EqualTo(Block0));

            Assert.That(reverseDiff.TryGetAccount(TestItem.AddressA, out Account? capturedAccount), Is.True);
            Assert.That(capturedAccount, Is.EqualTo(oldAccount));
            Assert.That(reverseDiff.TryGetAccount(TestItem.AddressB, out Account? absentAccount), Is.True, "absent old account needs a null marker");
            Assert.That(absentAccount, Is.Null);

            Assert.That(reverseDiff.TryGetStorage((TestItem.AddressA, (UInt256)1), out SlotValue? capturedSlot), Is.True);
            Assert.That(capturedSlot?.ToEvmBytes(), Is.EqualTo(oldSlot.ToEvmBytes()));
            Assert.That(reverseDiff.TryGetStorage((TestItem.AddressA, (UInt256)2), out SlotValue? absentSlot), Is.True, "absent old slot needs a null marker");
            Assert.That(absentSlot, Is.Null);

            Assert.That(reverseDiff.TryGetStateNode(presentPath, out TrieNode? capturedNode), Is.True);
            Assert.That(capturedNode?.FullRlp.ToArray(), Is.EqualTo(oldNodeRlp));
            Assert.That(capturedNode?.IsPersisted, Is.True);
            Assert.That(reverseDiff.TryGetStateNode(absentPath, out _), Is.False, "node absent at old state is skipped, not marked");

            Assert.That(reverseDiff.TryGetStorageNode((TestItem.KeccakA, presentPath), out TrieNode? capturedStorageNode), Is.True);
            Assert.That(capturedStorageNode?.FullRlp.ToArray(), Is.EqualTo(oldStorageNodeRlp));
        }
    }

    [Test]
    public void PersistSnapshot_EarlyPersist_SelfDestruct([Values] bool isNewAccount)
    {
        _config.EarlyPersist = true;
        PersistenceManager pm = CreateManager();
        _persistence.CreateReader().Returns(new FakePersistenceReader { CurrentState = Block0 });

        // Pre-existing history that an irreversible self-destruct must truncate.
        StateId priorBoundary = CreateStateId(4);
        StateId priorPersisted = CreateStateId(8);
        _snapshotRepository.TryAddReverseDiff(_resourcePool.CreateSnapshot(priorPersisted, priorBoundary, ResourcePool.Usage.ReverseDiff));

        StateId to = CreateStateId(16);
        using Snapshot snapshot = _resourcePool.CreateSnapshot(Block0, to, ResourcePool.Usage.ReadOnlyProcessingEnv);
        snapshot.Content.Accounts[TestItem.AddressB] = new Account(1, 100);
        snapshot.Content.SelfDestructedStorageAddresses[TestItem.AddressA] = isNewAccount;

        FakeWriteBatch writeBatch = new();
        _persistence.CreateWriteBatch(Block0, to).Returns(writeBatch);

        pm.PersistSnapshot(snapshot);

        using SnapshotPooledList priorHistory = _snapshotRepository.AssembleHistoricalSnapshots(priorBoundary, priorPersisted, 1);
        using SnapshotPooledList newDiff = _snapshotRepository.AssembleHistoricalSnapshots(Block0, to, 1);
        using (Assert.EnterMultipleScope())
        {
            // Same-tx created account (true) is reversible: nothing was ever persisted for it.
            // An account with persisted storage (false) is not: the window is truncated instead.
            Assert.That(priorHistory.Count, Is.EqualTo(isNewAccount ? 1 : 0), "prior history");
            Assert.That(newDiff.Count, Is.EqualTo(isNewAccount ? 1 : 0), "new reverse diff");
            Assert.That(writeBatch.SelfDestructCalls, isNewAccount ? Is.Empty : Is.EqualTo(new[] { TestItem.AddressA }));
        }
    }

    #endregion

    #region Helper Classes

    private class TestFinalizedStateProvider : IFinalizedStateProvider
    {
        private long _finalizedBlockNumber;
        private readonly Dictionary<long, Hash256> _finalizedStateRoots = [];

        public long FinalizedBlockNumber => _finalizedBlockNumber;

        public void SetFinalizedBlockNumber(long blockNumber) => _finalizedBlockNumber = blockNumber;

        public void SetFinalizedStateRootAt(long blockNumber, Hash256 stateRoot) => _finalizedStateRoots[blockNumber] = stateRoot;

        public Hash256? GetFinalizedStateRootAt(long blockNumber) =>
            _finalizedStateRoots.TryGetValue(blockNumber, out Hash256? root) ? root : null;
    }

    #endregion
}
