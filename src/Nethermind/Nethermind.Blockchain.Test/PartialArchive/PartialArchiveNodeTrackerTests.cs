// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Blockchain.PartialArchive;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Trie;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test.PartialArchive;

public class PartialArchiveNodeTrackerTests
{
    private MemColumnsDb<PartialArchiveColumns> _archiveDb = null!;
    private MemDb _stateDb = null!;
    private NodeStorage _nodeStorage = null!;
    private PartialArchiveNodeTracker _tracker = null!;

    private static readonly TreePath PathA = TreePath.FromPath(TestItem.KeccakA.Bytes);
    private static readonly TreePath PathB = TreePath.FromPath(TestItem.KeccakB.Bytes);
    private static readonly Hash256 V1 = TestItem.KeccakC;
    private static readonly Hash256 V2 = TestItem.KeccakD;
    private static readonly Hash256 V3 = TestItem.KeccakE;

    [SetUp]
    public void Setup()
    {
        _archiveDb = new MemColumnsDb<PartialArchiveColumns>();
        _stateDb = new MemDb();
        _nodeStorage = new NodeStorage(_stateDb);
        _tracker = CreateTracker();
    }

    [TearDown]
    public void TearDown()
    {
        _tracker.Dispose();
        _archiveDb.Dispose();
        _stateDb.Dispose();
    }

    private PartialArchiveNodeTracker CreateTracker() =>
        new(_archiveDb, new SingleStateDbProvider(_stateDb), _nodeStorage, LimboLogs.Instance);

    private void PersistNode(Hash256? address, in TreePath path, Hash256 keccak, ulong blockNumber)
    {
        _nodeStorage.Set(address, in path, keccak, [1, 2, 3]);
        _tracker.OnNodePersisted(address, in path, keccak, blockNumber);
    }

    private bool NodeExists(Hash256? address, in TreePath path, Hash256 keccak) =>
        _nodeStorage.Get(address, in path, keccak) is not null;

    private void PruneAndDrain(ulong cutoff, ulong barrierBlock)
    {
        // Barrier both marks the snapshot boundary (prune cutoff cap) and drains the queue;
        // the second barrier guarantees the prune command itself has been executed.
        _tracker.OnSnapshotPersisted(barrierBlock);
        Assert.That(_tracker.RequestPrune(cutoff), Is.True);
        _tracker.OnSnapshotPersisted(barrierBlock);
    }

    [Test]
    public void Deletes_superseded_version_when_it_leaves_the_window()
    {
        PersistNode(null, in PathA, V1, 1);
        PersistNode(null, in PathA, V2, 2);

        PruneAndDrain(cutoff: 5, barrierBlock: 5);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(NodeExists(null, in PathA, V1), Is.False, "superseded version should be deleted");
            Assert.That(NodeExists(null, in PathA, V2), Is.True, "current version must survive");
            Assert.That(_tracker.OldestRetainedBlock, Is.EqualTo(2UL));
        }
    }

    [Test]
    public void Keeps_superseded_version_while_inside_the_window()
    {
        PersistNode(null, in PathA, V1, 10);
        PersistNode(null, in PathA, V2, 20);

        PruneAndDrain(cutoff: 15, barrierBlock: 20);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(NodeExists(null, in PathA, V1), Is.True, "still needed by blocks 10..19");
            Assert.That(NodeExists(null, in PathA, V2), Is.True);
        }
    }

    [Test]
    public void Keeps_resurrected_version_and_deletes_the_intermediate_one()
    {
        PersistNode(null, in PathA, V1, 1);
        PersistNode(null, in PathA, V2, 2);
        PersistNode(null, in PathA, V1, 3);

        PruneAndDrain(cutoff: 10, barrierBlock: 10);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(NodeExists(null, in PathA, V1), Is.True, "resurrected version is current and must be kept");
            Assert.That(NodeExists(null, in PathA, V2), Is.False, "intermediate version should be deleted");
        }
    }

    [Test]
    public void Handles_out_of_order_events_recommit_overtaking_persistence()
    {
        // Block 3 re-commits V1 (reported at commit time) before blocks 1 and 2 are persisted.
        _nodeStorage.Set(null, in PathA, V1, [1, 2, 3]);
        _tracker.OnNodeRecommitted(null, in PathA, V1, 3);
        _tracker.OnNodePersisted(null, in PathA, V1, 1);
        PersistNode(null, in PathA, V2, 2);

        PruneAndDrain(cutoff: 10, barrierBlock: 10);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(NodeExists(null, in PathA, V1), Is.True, "V1 is current again since block 3");
            Assert.That(NodeExists(null, in PathA, V2), Is.False, "V2 was superseded at block 3");
        }
    }

    [Test]
    public void Never_deletes_base_versions_it_has_not_seen()
    {
        // V1 pre-exists (e.g. from snap sync) and is unknown to the tracker.
        _nodeStorage.Set(null, in PathA, V1, [9, 9, 9]);

        PersistNode(null, in PathA, V2, 5);
        PruneAndDrain(cutoff: 100, barrierBlock: 100);

        Assert.That(NodeExists(null, in PathA, V1), Is.True, "unknown base versions are leaked, never deleted");
    }

    [Test]
    public void Prune_executes_only_at_persistence_barriers()
    {
        PersistNode(null, in PathA, V1, 1);
        PersistNode(null, in PathA, V2, 2);
        _tracker.OnSnapshotPersisted(5);

        // A request alone must not delete anything: deletions are deferred to the next barrier,
        // where the persistence thread is parked and cannot race them.
        Assert.That(_tracker.RequestPrune(5), Is.True);
        Assert.That(NodeExists(null, in PathA, V1), Is.True);

        _tracker.OnSnapshotPersisted(5);
        Assert.That(NodeExists(null, in PathA, V1), Is.False);
    }

    [Test]
    public void Prune_cutoff_is_capped_by_last_persisted_snapshot()
    {
        PersistNode(null, in PathA, V1, 1);
        PersistNode(null, in PathA, V2, 2);

        // No barrier yet -> nothing is provably persisted -> nothing may be pruned.
        Assert.That(_tracker.RequestPrune(100), Is.True);
        _tracker.OnSnapshotPersisted(0);

        Assert.That(NodeExists(null, in PathA, V1), Is.True);
    }

    [Test]
    public void Tracks_storage_trie_nodes_independently_per_address()
    {
        Hash256 addressA = TestItem.KeccakF;

        PersistNode(addressA, in PathA, V1, 1);
        PersistNode(null, in PathA, V1, 1);
        PersistNode(addressA, in PathA, V2, 2);

        PruneAndDrain(cutoff: 10, barrierBlock: 10);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(NodeExists(addressA, in PathA, V1), Is.False, "superseded storage node should be deleted");
            Assert.That(NodeExists(addressA, in PathA, V2), Is.True);
            Assert.That(NodeExists(null, in PathA, V1), Is.True, "state-trie node at the same path is unrelated");
        }
    }

    [Test]
    public void Persists_retention_floor_across_restarts()
    {
        PersistNode(null, in PathA, V1, 1);
        PersistNode(null, in PathA, V2, 2);
        PersistNode(null, in PathB, V1, 3);
        PersistNode(null, in PathB, V3, 4);

        PruneAndDrain(cutoff: 10, barrierBlock: 10);
        ulong floor = _tracker.OldestRetainedBlock;
        Assert.That(floor, Is.EqualTo(4UL));

        _tracker.Dispose();
        _tracker = CreateTracker();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_tracker.OldestRetainedBlock, Is.EqualTo(floor));
            Assert.That(_tracker.LastSnapshotBlock, Is.EqualTo(10UL));
        }
    }

    [Test]
    public void Resets_tracking_when_state_database_identity_changes()
    {
        PersistNode(null, in PathA, V1, 1);
        PersistNode(null, in PathA, V2, 2);
        _tracker.OnSnapshotPersisted(5);
        _tracker.Dispose();

        // Full pruning / resync drops the stamp from the state DB.
        _stateDb.Remove(PartialArchiveNodeTracker.StateStampKey);
        _tracker = CreateTracker();

        Assert.That(_tracker.LastSnapshotBlock, Is.EqualTo(0UL));

        PruneAndDrain(cutoff: 10, barrierBlock: 10);
        Assert.That(NodeExists(null, in PathA, V1), Is.True, "stale journal must be discarded, not applied");
    }

    [Test]
    public void Alternating_versions_are_kept_while_referenced_inside_the_window()
    {
        // A path toggling between two values: both versions are referenced by blocks inside the
        // window, so neither may be deleted even though each was "superseded" long ago.
        for (ulong block = 1; block <= 10; block++)
        {
            PersistNode(null, in PathA, block % 2 == 0 ? V2 : V1, block);
        }

        PruneAndDrain(cutoff: 9, barrierBlock: 10);

        using (Assert.EnterMultipleScope())
        {
            // V1's latest supersession is block 10 (> cutoff); V2's is block 9 (<= cutoff, so
            // V2 is not needed by any block >= 9 where the value is V1... except block 10 flips
            // back to V2, which re-supersedes V1 — V2 is current and must survive regardless.
            Assert.That(NodeExists(null, in PathA, V2), Is.True, "current version");
            Assert.That(NodeExists(null, in PathA, V1), Is.True, "superseded at block 10, still inside window");
        }

        PruneAndDrain(cutoff: 10, barrierBlock: 11);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(NodeExists(null, in PathA, V2), Is.True, "still current");
            Assert.That(NodeExists(null, in PathA, V1), Is.False, "last supersession (block 10) left the window");
        }
    }

    [Test]
    public void Same_version_persisted_at_multiple_blocks_is_not_journaled()
    {
        PersistNode(null, in PathA, V1, 1);
        _tracker.OnNodePersisted(null, in PathA, V1, 5);

        PruneAndDrain(cutoff: 100, barrierBlock: 100);

        Assert.That(NodeExists(null, in PathA, V1), Is.True);
    }

    [Test]
    public void Large_backlog_is_pruned_in_self_rescheduling_slices()
    {
        int count = PartialArchiveNodeTracker.PruneRowBudget + 100;
        Span<byte> pathBytes = stackalloc byte[32];
        for (int i = 0; i < count; i++)
        {
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(pathBytes, i);
            TreePath path = TreePath.FromPath(pathBytes);
            PersistNode(null, in path, V1, 1);
            PersistNode(null, in path, V2, 2);
        }

        PruneAndDrain(cutoff: 10, barrierBlock: 10);
        // The follow-up slice was self-enqueued; drain it too.
        _tracker.OnSnapshotPersisted(10);
        _tracker.OnSnapshotPersisted(10);

        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(pathBytes, count - 1);
        TreePath lastPath = TreePath.FromPath(pathBytes);
        Assert.That(NodeExists(null, in lastPath, V1), Is.False, "backlog beyond one slice should still be pruned");
    }
}
