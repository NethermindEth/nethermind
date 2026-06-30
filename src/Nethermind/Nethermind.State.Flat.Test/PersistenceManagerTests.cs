// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State.Flat.History;
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

        _persistenceManager = new PersistenceManager(
            _config,
            ScheduleHelper.CreateWithOffset(_config, 0),
            _finalizedStateProvider,
            _persistence,
            _snapshotRepository,
            LimboLogs.Instance);
    }

    [TearDown]
    public void TearDown()
    {
    }

    private StateId CreateStateId(ulong blockNumber, byte rootByte = 0)
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
        ulong targetBlock = useCompacted ? 16UL : 1UL; // compacted uses 16, fallback uses 1
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

    #region Fresh DB (nothing persisted) Tests

    [Test]
    public void DetermineSnapshotToPersist_FreshDbBelowForceLimit_PersistsFinalizedBoundary()
    {
        IPersistence.IPersistenceReader reader = Substitute.For<IPersistence.IPersistenceReader>();
        reader.CurrentState.Returns(StateId.PreGenesis);

        IPersistence persistence = Substitute.For<IPersistence>();
        persistence.CreateReader().Returns(reader);

        ICompactionSchedule scheduler = ScheduleHelper.CreateWithOffset(_config, 0);
        PersistenceManager pm = new(_config, scheduler, _finalizedStateProvider, persistence, _snapshotRepository, LimboLogs.Instance);

        // Depth 101 is past MinReorgDepth + CompactSize (80) but below the MaxReorgDepth force limit (256),
        // so only the finalized branch can produce a snapshot here.
        StateId target = CreateStateId(16);
        StateId latest = CreateStateId(100);
        _finalizedStateProvider.SetFinalizedBlockNumber(100);
        _finalizedStateProvider.SetFinalizedStateRootAt(16, new Hash256(target.StateRoot.Bytes));

        using Snapshot expected = CreateSnapshot(StateId.PreGenesis, target, compacted: true);

        Snapshot? result = pm.DetermineSnapshotToPersist(latest);

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.From, Is.EqualTo(StateId.PreGenesis));
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
        ulong targetBlock = useCompacted ? 16UL : 1UL; // compacted uses 16, fallback uses 1
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

    [Test]
    public void DetermineSnapshotToPersist_UnfinalizedForkAtBoundary_PersistsHeadReachableFork()
    {
        // Two unfinalized forks at the boundary block 16, both starting from Block0. The head's chain runs
        // through target2 (the higher root, not the arbitrary "first"). The forced persist must follow the
        // head's chain (target2), otherwise persisting target1 would orphan the head.
        StateId persisted = Block0;
        StateId target1 = CreateStateId(16, rootByte: 1); // arbitrary "first" (lowest root)
        StateId target2 = CreateStateId(16, rootByte: 2); // on the head's chain
        StateId head = CreateStateId(300);

        _finalizedStateProvider.SetFinalizedBlockNumber(10); // unfinalized at the boundary

        using Snapshot fork1 = CreateSnapshot(persisted, target1, compacted: true);
        using Snapshot fork2 = CreateSnapshot(persisted, target2, compacted: true);
        using Snapshot toHead = CreateSnapshot(target2, head, compacted: true); // head reachable only via target2
        _snapshotRepository.SetLastCommittedStateId(head);

        Snapshot? result = _persistenceManager.DetermineSnapshotToPersist(head);

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.From, Is.EqualTo(persisted));
        Assert.That(result.To, Is.EqualTo(target2));

        result.Dispose();
    }

    [Test]
    public void DetermineSnapshotToPersist_LongerNonCanonicalFork_PersistsCommittedHeadChain()
    {
        // The longest in-memory chain runs through target1 up to block 300, but the committed head is the
        // shorter chain through target2 (at block 32). The forced persist must follow the committed head
        // (target2), not the longer fork (target1) that GetLastSnapshotId would pick.
        StateId persisted = Block0;
        StateId target1 = CreateStateId(16, rootByte: 1); // boundary state on the longer, non-canonical fork
        StateId target2 = CreateStateId(16, rootByte: 2); // boundary state on the committed head's chain
        StateId longHead = CreateStateId(300); // longest chain (the max), but not committed
        StateId committedHead = CreateStateId(32, rootByte: 2);

        _finalizedStateProvider.SetFinalizedBlockNumber(0); // unfinalized at the boundary

        using Snapshot fork1 = CreateSnapshot(persisted, target1, compacted: true);
        using Snapshot fork2 = CreateSnapshot(persisted, target2, compacted: true);
        using Snapshot toLongHead = CreateSnapshot(target1, longHead, compacted: true); // makes target1 the max chain
        using Snapshot toCommittedHead = CreateSnapshot(target2, committedHead, compacted: true);
        _snapshotRepository.SetLastCommittedStateId(committedHead);

        // latestSnapshot at 300 (the longest chain) makes the in-memory depth exceed MaxReorgDepth (256),
        // triggering the force-persist branch.
        Snapshot? result = _persistenceManager.DetermineSnapshotToPersist(longHead);

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.From, Is.EqualTo(persisted));
        Assert.That(result.To, Is.EqualTo(target2));

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
    public void DetermineSnapshotToPersist_LatestSnapshotBelowPersistedBlock_ReturnsNullWithoutUnderflow()
    {
        // A deep reorg below a force-persisted unfinalized block can leave the latest snapshot
        // behind the last persisted block. The in-memory depth must saturate to 0 (not underflow to ~2^64),
        // so the keep-in-memory guard returns null early instead of force-persisting a stale head ancestor.
        StateId persisted = CreateStateId(100);

        IPersistence.IPersistenceReader reader = Substitute.For<IPersistence.IPersistenceReader>();
        reader.CurrentState.Returns(persisted);

        IPersistence persistence = Substitute.For<IPersistence>();
        persistence.CreateReader().Returns(reader);

        PersistenceManager pm = new(_config, ScheduleHelper.CreateWithOffset(_config, 0), _finalizedStateProvider, persistence, _snapshotRepository, LimboLogs.Instance);

        // Latest snapshot (50) is below the persisted block (100); finalized far behind so the force-persist
        // branch would be taken on underflow. Stage a head-ancestor snapshot the buggy path would return.
        StateId latest = CreateStateId(50);
        StateId headAncestor = CreateStateId(101);
        _finalizedStateProvider.SetFinalizedBlockNumber(10);

        using Snapshot staged = CreateSnapshot(persisted, headAncestor, compacted: false);
        _snapshotRepository.SetLastCommittedStateId(headAncestor);

        Snapshot? result = pm.DetermineSnapshotToPersist(latest);
        using Snapshot? _ = result; // dispose if the buggy underflow path returned a snapshot

        Assert.That(result, Is.Null);
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

    [TestCase(3, 13UL)]
    [TestCase(5, 11UL)]
    [TestCase(0, 16UL)]
    public void DetermineSnapshotToPersist_WithOffset_FirstBoundaryShifted(int offset, ulong expectedTargetBlock)
    {
        // Fresh DB: currentPersistedState = Block0 (block 0).
        // With CompactSize=16 and offset=N, the next full compaction boundary is at block 16-N.
        PersistenceManager pm = new(
            _config,
            ScheduleHelper.CreateWithOffset(_config, offset),
            _finalizedStateProvider,
            _persistence,
            _snapshotRepository,
            LimboLogs.Instance);

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
    public void FlushToPersistence_UnfinalizedForkAtBoundary_PersistsHeadReachableFork()
    {
        // Two unfinalized forks at the boundary block 16; the head's chain runs through target2. The flush
        // must persist target2 (head-reachable), not the arbitrary first fork target1.
        StateId target1 = CreateStateId(16, rootByte: 1); // arbitrary "first" (lowest root)
        StateId target2 = CreateStateId(16, rootByte: 2); // on the head's chain
        StateId head = CreateStateId(32);

        _finalizedStateProvider.SetFinalizedBlockNumber(0); // nothing finalized

        using Snapshot fork1 = CreateSnapshot(Block0, target1, compacted: true);
        using Snapshot fork2 = CreateSnapshot(Block0, target2, compacted: true);
        using Snapshot toHead = CreateSnapshot(target2, head, compacted: true); // head reachable only via target2
        _snapshotRepository.SetLastCommittedStateId(head);

        IPersistence.IWriteBatch writeBatch = Substitute.For<IPersistence.IWriteBatch>();
        _persistence.CreateWriteBatch(Arg.Any<StateId>(), Arg.Any<StateId>()).Returns(writeBatch);

        StateId result = _persistenceManager.FlushToPersistence();

        Assert.That(result, Is.EqualTo(head));
        _persistence.Received().CreateWriteBatch(Block0, target2);
        _persistence.DidNotReceive().CreateWriteBatch(Block0, target1);
    }

    [Test]
    public void FlushToPersistence_LongerNonCanonicalFork_PersistsCommittedHeadChain()
    {
        // The longest in-memory chain runs through target1 to block 300, but the committed head is the
        // shorter chain through target2 (at block 32). The flush must follow the committed head (target2),
        // stopping at its block, not chase the longer non-canonical fork through target1.
        StateId target1 = CreateStateId(16, rootByte: 1); // boundary state on the longer, non-canonical fork
        StateId target2 = CreateStateId(16, rootByte: 2); // boundary state on the committed head's chain
        StateId longHead = CreateStateId(300); // longest chain (the max), but not committed
        StateId committedHead = CreateStateId(32, rootByte: 2);

        _finalizedStateProvider.SetFinalizedBlockNumber(0); // nothing finalized

        using Snapshot fork1 = CreateSnapshot(Block0, target1, compacted: true);
        using Snapshot fork2 = CreateSnapshot(Block0, target2, compacted: true);
        // Not `using`: the flush prunes this orphaned non-canonical descendant and disposes it itself.
        Snapshot toLongHead = CreateSnapshot(target1, longHead, compacted: true);
        using Snapshot toCommittedHead = CreateSnapshot(target2, committedHead, compacted: true);
        _snapshotRepository.SetLastCommittedStateId(committedHead);

        IPersistence.IWriteBatch writeBatch = Substitute.For<IPersistence.IWriteBatch>();
        _persistence.CreateWriteBatch(Arg.Any<StateId>(), Arg.Any<StateId>()).Returns(writeBatch);

        StateId result = _persistenceManager.FlushToPersistence();

        Assert.That(result, Is.EqualTo(committedHead));
        _persistence.Received().CreateWriteBatch(Block0, target2);
        _persistence.DidNotReceive().CreateWriteBatch(Block0, target1);
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

    [Test]
    public void AddToPersistence_CapturesHistoryUpToPersistedBlock()
    {
        using SnapshotableMemColumnsDb<FlatDbColumns> historyDb = new();
        using SnapshotableMemColumnsDb<FlatHistoryColumns> historyColumns = new();
        HistoryWriter historyWriter = new(historyDb, historyColumns, new FlatDbConfig { HistoryEnabled = true }, LimboLogs.Instance);

        IPersistence persistence = Substitute.For<IPersistence>();
        IPersistence.IPersistenceReader reader = Substitute.For<IPersistence.IPersistenceReader>();
        reader.CurrentState.Returns(CreateStateId(5));
        persistence.CreateReader().Returns(reader);

        PersistenceManager manager = new(
            _config,
            ScheduleHelper.CreateWithOffset(_config, 0),
            _finalizedStateProvider,
            persistence,
            _snapshotRepository,
            LimboLogs.Instance,
            historyWriter);

        manager.AddToPersistence(CreateStateId(5));

        Assert.That(historyWriter.LastCapturedBlock, Is.EqualTo(5UL));
    }

    #endregion

    #region Helper Classes

    private class TestFinalizedStateProvider : IFinalizedStateProvider
    {
        private ulong _finalizedBlockNumber;
        private readonly Dictionary<ulong, Hash256> _finalizedStateRoots = [];

        public ulong FinalizedBlockNumber => _finalizedBlockNumber;

        public void SetFinalizedBlockNumber(ulong blockNumber) => _finalizedBlockNumber = blockNumber;

        public void SetFinalizedStateRootAt(ulong blockNumber, Hash256 stateRoot) => _finalizedStateRoots[blockNumber] = stateRoot;

        public Hash256? GetFinalizedStateRootAt(ulong blockNumber) =>
            _finalizedStateRoots.TryGetValue(blockNumber, out Hash256? root) ? root : null;
    }

    #endregion
}
