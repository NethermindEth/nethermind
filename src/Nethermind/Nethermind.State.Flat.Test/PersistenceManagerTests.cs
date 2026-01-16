// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Collections;
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
    private CancellationTokenSource _cts = null!;
    private StateId Block0 = new StateId(0, Keccak.EmptyTreeHash);

    [SetUp]
    public void SetUp()
    {
        _config = new FlatDbConfig
        {
            CompactSize = 16,
            PruningBoundary = 64,
            MaxPruningBoundary = 256
        };

        _resourcePool = new ResourcePool(_config);
        _finalizedStateProvider = new TestFinalizedStateProvider();
        _snapshotRepository = new SnapshotRepository(LimboLogs.Instance);
        _persistence = Substitute.For<IPersistence>();
        _cts = new CancellationTokenSource();

        var persistenceReader = Substitute.For<IPersistence.IPersistenceReader>();
        persistenceReader.CurrentState.Returns(Block0);
        _persistence.CreateReader().Returns(persistenceReader);

        var processExitSource = Substitute.For<IProcessExitSource>();
        processExitSource.Token.Returns(_cts.Token);

        _persistenceManager = new PersistenceManager(
            _config,
            _finalizedStateProvider,
            _persistence,
            _snapshotRepository,
            processExitSource,
            LimboLogs.Instance);
    }

    [TearDown]
    public async Task TearDown()
    {
        _cts.Cancel();
        await _persistenceManager.DisposeAsync();
        _cts.Dispose();
    }

    private StateId CreateStateId(long blockNumber, byte rootByte = 0)
    {
        byte[] bytes = new byte[32];
        bytes[0] = rootByte;
        return new StateId(blockNumber, new ValueHash256(bytes));
    }

    private Snapshot CreateSnapshot(StateId from, StateId to, bool compacted = false)
    {
        var snapshot = _resourcePool.CreateSnapshot(from, to, ResourcePool.Usage.ReadOnlyProcessingEnv);
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
        var snapshot = _resourcePool.CreateSnapshot(from, to, ResourcePool.Usage.ReadOnlyProcessingEnv);
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

        var result = _persistenceManager.DetermineSnapshotToPersist(latest);

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
        using var expectedSnapshot = CreateSnapshot(persisted, target, compacted: useCompacted);

        var result = _persistenceManager.DetermineSnapshotToPersist(latest);

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

        var result = _persistenceManager.DetermineSnapshotToPersist(latest);

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
        using var expectedSnapshot = CreateSnapshot(persisted, target, compacted: useCompacted);

        var result = _persistenceManager.DetermineSnapshotToPersist(latest);

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

        var result = _persistenceManager.DetermineSnapshotToPersist(latest);

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
        using var wrongSnapshot = CreateSnapshot(wrongFrom, target, compacted: true);

        var result = _persistenceManager.DetermineSnapshotToPersist(latest);

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
        using var snapshot1 = CreateSnapshot(persisted, target1, compacted: true);
        using var snapshot2 = CreateSnapshot(persisted, target2, compacted: true);

        var result = _persistenceManager.DetermineSnapshotToPersist(latest);

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

        var result = _persistenceManager.DetermineSnapshotToPersist(latest);

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

        using var expectedSnapshot = CreateSnapshot(persisted, target, compacted: true);

        var result = _persistenceManager.DetermineSnapshotToPersist(latest);

        Assert.That(result, Is.Not.Null);

        result!.Dispose();
    }

    #endregion

    #region PersistSnapshot Tests

    [Test]
    public void PersistSnapshot_WithAccountsStorageAndTrieNodes_WritesToBatch()
    {
        // Arrange
        var from = Block0;
        var to = CreateStateId(16);
        using var snapshot = _resourcePool.CreateSnapshot(from, to, ResourcePool.Usage.ReadOnlyProcessingEnv);

        // Add accounts
        snapshot.Content.Accounts[TestItem.AddressA] = new Account(1, 100);
        snapshot.Content.Accounts[TestItem.AddressB] = new Account(2, 200);

        // Add storage
        snapshot.Content.Storages[(TestItem.AddressA, (UInt256)1)] = SlotValue.FromSpanWithoutLeadingZero([42]);
        snapshot.Content.Storages[(TestItem.AddressA, (UInt256)2)] = SlotValue.FromSpanWithoutLeadingZero([99]);

        // Add trie nodes
        var path = TreePath.Empty;
        var node = new TrieNode(NodeType.Leaf, Keccak.Zero);
        snapshot.Content.StateNodes[path] = node;

        var writeBatch = Substitute.For<IPersistence.IWriteBatch>();
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
        var from = Block0;
        var to = CreateStateId(16);
        using var snapshot = CreateSnapshotWithSelfDestruct(from, to);

        var writeBatch = Substitute.For<IPersistence.IWriteBatch>();
        writeBatch.SelfDestruct(Arg.Any<Address>()).Returns(1);
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
        var from = Block0;
        var to = CreateStateId(16);
        using var snapshot = _resourcePool.CreateSnapshot(from, to, ResourcePool.Usage.ReadOnlyProcessingEnv);

        var writeBatch = Substitute.For<IPersistence.IWriteBatch>();
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
        var from = Block0;
        var to = CreateStateId(16);
        var latest = CreateStateId(100);

        // Create a snapshot that should be persisted
        using var snapshot = CreateSnapshot(from, to, compacted: true);

        _finalizedStateProvider.SetFinalizedBlockNumber(100);
        _finalizedStateProvider.SetFinalizedStateRootAt(16, new Hash256(to.StateRoot.Bytes));

        var writeBatch = Substitute.For<IPersistence.IWriteBatch>();
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

    #region Helper Classes

    private class TestFinalizedStateProvider : IFinalizedStateProvider
    {
        private long _finalizedBlockNumber;
        private readonly Dictionary<long, Hash256> _finalizedStateRoots = new();

        public long FinalizedBlockNumber => _finalizedBlockNumber;

        public void SetFinalizedBlockNumber(long blockNumber)
        {
            _finalizedBlockNumber = blockNumber;
        }

        public void SetFinalizedStateRootAt(long blockNumber, Hash256 stateRoot)
        {
            _finalizedStateRoots[blockNumber] = stateRoot;
        }

        public Hash256? GetFinalizedStateRootAt(long blockNumber)
        {
            return _finalizedStateRoots.TryGetValue(blockNumber, out var root) ? root : null;
        }
    }

    #endregion
}
