// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Logging;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test;

[TestFixture]
public class SnapshotRepositoryTests
{
    private SnapshotRepository _repository = null!;
    private ResourcePool _resourcePool = null!;
    private FlatDbConfig _config = null!;

    [SetUp]
    public void SetUp()
    {
        _config = new FlatDbConfig { CompactSize = 16 };
        _resourcePool = new ResourcePool(_config);
        _repository = new SnapshotRepository(LimboLogs.Instance);
    }

    private StateId CreateStateId(long blockNumber, byte rootByte = 0)
    {
        byte[] bytes = new byte[32];
        bytes[0] = rootByte;
        return new StateId(blockNumber, new ValueHash256(bytes));
    }

    private Snapshot CreateSnapshot(StateId from, StateId to, bool withData = false)
    {
        Snapshot snapshot = _resourcePool.CreateSnapshot(from, to, ResourcePool.Usage.ReadOnlyProcessingEnv);
        if (withData)
        {
            snapshot.Content.Accounts[TestItem.AddressA] = new Account(1, 100);
        }
        return snapshot;
    }

    private Snapshot AddSnapshotToRepository(long fromBlock, long toBlock, bool compacted = false, bool withData = false)
        => AddSnapshotToRepository(CreateStateId(fromBlock), CreateStateId(toBlock), compacted, withData);

    private Snapshot AddSnapshotToRepository(StateId from, StateId to, bool compacted = false, bool withData = false)
    {
        Snapshot snapshot = CreateSnapshot(from, to, withData);

        bool added = compacted
            ? _repository.TryAddCompactedSnapshot(snapshot)
            : _repository.TryAddSnapshot(snapshot);

        Assert.That(added, Is.True, $"Failed to add snapshot {from}->{to}");

        if (!compacted)
        {
            _repository.AddStateId(to);
        }

        return snapshot;
    }

    private bool TryLease(StateId state, bool compacted, out Snapshot? snapshot)
        => compacted
            ? _repository.TryLeaseCompactedState(state, out snapshot)
            : _repository.TryLeaseState(state, out snapshot);

    private List<Snapshot> BuildSnapshotChain(long startBlock, long endBlock)
    {
        List<Snapshot> snapshots = [];
        for (long i = startBlock; i < endBlock; i++)
        {
            snapshots.Add(AddSnapshotToRepository(i, i + 1));
        }
        return snapshots;
    }

    private void BuildSnapshotChain(StateId start, long endBlock, byte rootByte = 0)
    {
        StateId prev = start;
        for (long block = start.BlockNumber + 1; block <= endBlock; block++)
        {
            StateId next = CreateStateId(block, rootByte);
            AddSnapshotToRepository(prev, next);
            prev = next;
        }
    }

    #region Snapshot Addition and Removal

    [Test]
    public void TryAddSnapshot_NewAndDuplicate_BehavesCorrectly([Values] bool compacted)
    {
        StateId from = CreateStateId(0);
        StateId to = CreateStateId(1);
        Snapshot snapshot1 = CreateSnapshot(from, to);
        Snapshot snapshot2 = CreateSnapshot(from, to);

        bool added1 = compacted ? _repository.TryAddCompactedSnapshot(snapshot1) : _repository.TryAddSnapshot(snapshot1);
        bool added2 = compacted ? _repository.TryAddCompactedSnapshot(snapshot2) : _repository.TryAddSnapshot(snapshot2);

        Assert.That(added1, Is.True);
        Assert.That(added2, Is.False);

        snapshot2.Dispose();
    }

    [Test]
    public void AddAndRemoveSnapshot_CannotLeaseAfterRemoval()
    {
        StateId from = CreateStateId(0);
        StateId to = CreateStateId(1);
        Snapshot snapshot = CreateSnapshot(from, to);
        _repository.AddStateId(to);

        _repository.TryAddSnapshot(snapshot);
        bool leasedBefore = _repository.TryLeaseState(to, out Snapshot? leasedSnapshot);
        leasedSnapshot?.Dispose();

        _repository.RemoveAndReleaseKnownState(to);
        bool leasedAfter = _repository.TryLeaseState(to, out _);

        Assert.That(leasedBefore, Is.True);
        Assert.That(leasedAfter, Is.False);
    }

    [Test]
    public void RemoveSnapshot_WithActiveLeases_DisposesWhenAllReleased()
    {
        AddSnapshotToRepository(0, 1);
        StateId to = CreateStateId(1);

        bool leased1 = _repository.TryLeaseState(to, out Snapshot? snapshot1);
        bool leased2 = _repository.TryLeaseState(to, out Snapshot? snapshot2);

        Assert.That(leased1, Is.True);
        Assert.That(leased2, Is.True);

        _repository.RemoveAndReleaseKnownState(to);

        snapshot1!.Dispose();
        snapshot2!.Dispose();

        bool leasedAfter = _repository.TryLeaseState(to, out _);
        Assert.That(leasedAfter, Is.False);
    }

    #endregion

    #region Lease Operations

    [Test]
    public void TryLeaseState_ExistingAndNonExistent([Values] bool compacted)
    {
        AddSnapshotToRepository(0, 1, compacted: compacted);

        bool leasedExisting = TryLease(CreateStateId(1), compacted, out Snapshot? snapshot);
        Assert.That(leasedExisting, Is.True);
        Assert.That(snapshot, Is.Not.Null);
        snapshot!.Dispose();

        bool leasedNonExistent = TryLease(CreateStateId(999), compacted, out Snapshot? nonExistentSnapshot);
        Assert.That(leasedNonExistent, Is.False);
        Assert.That(nonExistentSnapshot, Is.Null);
    }

    [Test]
    public void TryLeaseState_MultipleLeases_AllSucceed([Values] bool compacted)
    {
        AddSnapshotToRepository(0, 1, compacted: compacted);

        StateId to = CreateStateId(1);
        bool leased1 = TryLease(to, compacted, out Snapshot? snapshot1);
        bool leased2 = TryLease(to, compacted, out Snapshot? snapshot2);
        bool leased3 = TryLease(to, compacted, out Snapshot? snapshot3);

        Assert.That(leased1, Is.True);
        Assert.That(leased2, Is.True);
        Assert.That(leased3, Is.True);

        Assert.That(snapshot1, Is.SameAs(snapshot2));
        Assert.That(snapshot2, Is.SameAs(snapshot3));

        snapshot1!.Dispose();
        snapshot2!.Dispose();
        snapshot3!.Dispose();
    }

    #endregion

    #region Query Operations

    [Test]
    public void HasState_ExistingAndNonExistent()
    {
        AddSnapshotToRepository(0, 1);
        StateId existing = CreateStateId(1);
        StateId nonExistent = CreateStateId(999);

        bool hasExisting = _repository.HasState(existing);
        bool hasNonExistent = _repository.HasState(nonExistent);

        Assert.That(hasExisting, Is.True);
        Assert.That(hasNonExistent, Is.False);
    }

    [Test]
    public void GetSnapshotBeforeStateId_EmptyRepository()
    {
        StateId target = CreateStateId(10);

        ArrayPoolList<StateId> states = _repository.GetSnapshotBeforeStateId(target);

        Assert.That(states.Count, Is.EqualTo(0));
        states.Dispose();
    }

    [Test]
    public void GetSnapshotBeforeStateId_NoStatesBeforeTarget()
    {
        StateId state10 = CreateStateId(10);
        _repository.AddStateId(state10);

        StateId target = CreateStateId(5);
        ArrayPoolList<StateId> states = _repository.GetSnapshotBeforeStateId(target);

        Assert.That(states.Count, Is.EqualTo(0));
        states.Dispose();
    }

    [Test]
    public void GetSnapshotBeforeStateId_StatesBeforeTarget()
    {
        StateId state1 = CreateStateId(1);
        StateId state3 = CreateStateId(3);
        StateId state5 = CreateStateId(5);
        StateId state7 = CreateStateId(7);
        StateId state10 = CreateStateId(10);

        _repository.AddStateId(state1);
        _repository.AddStateId(state3);
        _repository.AddStateId(state5);
        _repository.AddStateId(state7);
        _repository.AddStateId(state10);

        StateId target = CreateStateId(6);
        ArrayPoolList<StateId> states = _repository.GetSnapshotBeforeStateId(target);

        Assert.That(states.Count, Is.EqualTo(3));
        states.Dispose();
    }

    [TestCase(-1)]
    [TestCase(long.MinValue)]
    public void GetSnapshotBeforeStateId_NegativeBlockNumber_ReturnsEmpty(long blockNumber)
    {
        _repository.AddStateId(CreateStateId(1));

        StateId target = new(blockNumber, Keccak.EmptyTreeHash);
        ArrayPoolList<StateId> states = _repository.GetSnapshotBeforeStateId(target);

        Assert.That(states.Count, Is.EqualTo(0));
        states.Dispose();
    }

    #endregion

    #region AssembleSnapshotsUntil

    [Test]
    public void AssembleSnapshotsUntil_EmptyRepository()
    {
        StateId target = CreateStateId(10);

        using SnapshotPooledList assembled = _repository.AssembleSnapshotsUntil(target, 0, 10);

        Assert.That(assembled.Count, Is.EqualTo(0));
    }

    [Test]
    public void AssembleSnapshotsUntil_SingleSnapshot()
    {
        AddSnapshotToRepository(0, 1);

        StateId target = CreateStateId(1);
        using SnapshotPooledList assembled = _repository.AssembleSnapshotsUntil(target, 0, 10);

        Assert.That(assembled.Count, Is.EqualTo(1));
        Assert.That(assembled[0].To, Is.EqualTo(target));
    }

    [Test]
    public void AssembleSnapshotsUntil_LinearChain()
    {
        BuildSnapshotChain(0, 4);

        StateId target = CreateStateId(4);
        using SnapshotPooledList assembled = _repository.AssembleSnapshotsUntil(target, 0, 10);

        Assert.That(assembled.Count, Is.EqualTo(4));
    }

    [Test]
    public void AssembleSnapshotsUntil_StopsAtStartingBlock()
    {
        BuildSnapshotChain(0, 5);

        StateId target = CreateStateId(4);
        using SnapshotPooledList assembled = _repository.AssembleSnapshotsUntil(target, 2, 10);

        Assert.That(assembled.Count, Is.EqualTo(2));
    }

    [Test]
    public void AssembleSnapshotsUntil_PrefersCompacted()
    {
        StateId from = CreateStateId(0);
        StateId to = CreateStateId(1);

        Snapshot compacted = CreateSnapshot(from, to);
        _repository.TryAddCompactedSnapshot(compacted);

        using SnapshotPooledList assembled = _repository.AssembleSnapshotsUntil(to, 0, 10);

        Assert.That(assembled.Count, Is.EqualTo(1));
    }

    #endregion

    [Test]
    public void AssembleSnapshots_LinearChain_ReturnsAscendingPathToTarget()
    {
        BuildSnapshotChain(0, 5);

        using SnapshotPooledList assembled = _repository.AssembleSnapshots(CreateStateId(5), CreateStateId(0), 10);

        Assert.That(assembled.Count, Is.EqualTo(5));
        Assert.That(assembled[0].From, Is.EqualTo(CreateStateId(0)));
        Assert.That(assembled[^1].To, Is.EqualTo(CreateStateId(5)));
    }

    [Test]
    public void AssembleSnapshots_CompactedSnapshot_TakesWideHop()
    {
        AddSnapshotToRepository(0, 5, compacted: true);

        using SnapshotPooledList assembled = _repository.AssembleSnapshots(CreateStateId(5), CreateStateId(0), 10);

        Assert.That(assembled.Count, Is.EqualTo(1));
        Assert.That(assembled[0].From, Is.EqualTo(CreateStateId(0)));
        Assert.That(assembled[0].To, Is.EqualTo(CreateStateId(5)));
    }

    [Test]
    public void AssembleSnapshots_CompactedOvershoot_FallsBackToBaseEdges()
    {
        BuildSnapshotChain(0, 5);
        AddSnapshotToRepository(0, 5, compacted: true);

        using SnapshotPooledList assembled = _repository.AssembleSnapshots(CreateStateId(5), CreateStateId(2), 10);

        Assert.That(assembled.Count, Is.EqualTo(3));
        Assert.That(assembled[0].From, Is.EqualTo(CreateStateId(2)));
        Assert.That(assembled[^1].To, Is.EqualTo(CreateStateId(5)));
    }

    [Test]
    public void AssembleSnapshots_BaseEqualsTarget_ReturnsEmpty()
    {
        BuildSnapshotChain(0, 3);

        using SnapshotPooledList assembled = _repository.AssembleSnapshots(CreateStateId(3), CreateStateId(3), 10);

        Assert.That(assembled.Count, Is.EqualTo(0));
    }

    [Test]
    public void AssembleSnapshots_UnreachableTarget_ReturnsEmpty()
    {
        BuildSnapshotChain(1, 4);

        using SnapshotPooledList assembled = _repository.AssembleSnapshots(CreateStateId(4), CreateStateId(0), 10);

        Assert.That(assembled.Count, Is.EqualTo(0));
    }

    [Test]
    public void AssembleSnapshots_SelfReferencingSnapshot_ReturnsEmptyWithoutHanging()
    {
        AddSnapshotToRepository(CreateStateId(1), CreateStateId(1));

        using SnapshotPooledList assembled = _repository.AssembleSnapshots(CreateStateId(1), CreateStateId(0), 10);

        Assert.That(assembled.Count, Is.EqualTo(0));
    }

    [Test]
    public void RemoveSiblingAndDescendents_LinearChain_RemovesNothing()
    {
        BuildSnapshotChain(0, 10);

        _repository.RemoveSiblingAndDescendents(CreateStateId(5));

        for (long block = 1; block <= 10; block++)
        {
            Assert.That(_repository.HasState(CreateStateId(block)), Is.True, $"State {block} should be kept");
        }
    }

    [Test]
    public void RemoveSiblingAndDescendents_OrphanedFork_PrunesUnreachableDescendantsAbovePersistedBlock()
    {
        // Common 0->3, then canonical and non-canonical branches both diverging at block 3.
        // Persisting C(5) must prune NC descendants above block 5 (kept at/below — that's RemoveStatesUntil's job).
        BuildSnapshotChain(0, 3);
        BuildSnapshotChain(CreateStateId(3), 7);
        BuildSnapshotChain(CreateStateId(3), 7, rootByte: 1);

        _repository.RemoveSiblingAndDescendents(CreateStateId(5));

        Assert.That(_repository.HasState(CreateStateId(6, rootByte: 1)), Is.False, "orphan NC(6) should be pruned");
        Assert.That(_repository.HasState(CreateStateId(7, rootByte: 1)), Is.False, "orphan NC(7) should be pruned");
        Assert.That(_repository.HasState(CreateStateId(6)), Is.True, "canonical C(6) should be kept");
        Assert.That(_repository.HasState(CreateStateId(7)), Is.True, "canonical C(7) should be kept");
        Assert.That(_repository.HasState(CreateStateId(5, rootByte: 1)), Is.True, "NC(5) at the persisted block is left to RemoveStatesUntil");
        Assert.That(_repository.HasState(CreateStateId(4, rootByte: 1)), Is.True, "NC(4) below the persisted block is left to RemoveStatesUntil");
    }

    [Test]
    public void RemoveSiblingAndDescendents_ForkAbovePersistedBlock_KeepsBothBranches()
    {
        BuildSnapshotChain(0, 6);
        AddSnapshotToRepository(CreateStateId(6), CreateStateId(7));
        AddSnapshotToRepository(CreateStateId(6), CreateStateId(7, rootByte: 1));

        _repository.RemoveSiblingAndDescendents(CreateStateId(3));

        Assert.That(_repository.HasState(CreateStateId(7)), Is.True);
        Assert.That(_repository.HasState(CreateStateId(7, rootByte: 1)), Is.True);
    }

    private Snapshot AddReverseDiff(long fromBlock, long toBlock)
    {
        Snapshot reverseDiff = CreateSnapshot(CreateStateId(fromBlock), CreateStateId(toBlock));
        Assert.That(_repository.TryAddReverseDiff(reverseDiff), Is.True, $"Failed to add reverse diff {fromBlock}->{toBlock}");
        return reverseDiff;
    }

    /// <summary>
    /// Per-block chain 0..12 archived in chunks of 4 with reverse diffs (4→0), (8→4), (12→8).
    /// Historical forwards left: 1-3, 5-7, 9-11 (each chunk's upper boundary snapshot is released on archive).
    /// </summary>
    private void BuildArchivedChunks()
    {
        BuildSnapshotChain(0, 12);
        for (long boundary = 4; boundary <= 12; boundary += 4)
        {
            _repository.ArchiveStatesUntil(CreateStateId(boundary));
            AddReverseDiff(boundary, boundary - 4);
        }
    }

    private static void AssertContiguousChain(SnapshotPooledList assembled, StateId persisted, StateId baseState)
    {
        Assert.That(assembled[0].From, Is.EqualTo(persisted), "topmost reverse diff starts at the persisted state");
        Assert.That(assembled[^1].To, Is.EqualTo(baseState), "list ends at the requested state");
        for (int i = 1; i < assembled.Count; i++)
        {
            Assert.That(assembled[i].From, Is.EqualTo(assembled[i - 1].To), $"chain broken at index {i}");
        }
    }

    [Test]
    public void ArchiveStatesUntil_MovesCanonicalChainAndReleasesRest()
    {
        BuildSnapshotChain(0, 6);
        AddSnapshotToRepository(0, 4, compacted: true);
        AddSnapshotToRepository(CreateStateId(3), CreateStateId(4, rootByte: 1));

        _repository.ArchiveStatesUntil(CreateStateId(4));

        using (Assert.EnterMultipleScope())
        {
            for (long block = 1; block <= 4; block++)
            {
                Assert.That(_repository.HasState(CreateStateId(block)), Is.False, $"state {block} should leave the live set");
            }
            for (long block = 1; block <= 3; block++)
            {
                Assert.That(_repository.HasHistoricalState(CreateStateId(block)), Is.True, $"state {block} should be historical");
            }
            Assert.That(_repository.HasHistoricalState(CreateStateId(4)), Is.False, "persisted state's snapshot is released, not archived");
            Assert.That(_repository.TryLeaseCompactedState(CreateStateId(4), out _), Is.False, "compacted snapshot should be released");
            Assert.That(_repository.HasState(CreateStateId(4, rootByte: 1)), Is.False, "non-canonical leftover should be released");
            Assert.That(_repository.HasState(CreateStateId(5)), Is.True, "states above the persisted block stay live");
            Assert.That(_repository.HasState(CreateStateId(6)), Is.True, "states above the persisted block stay live");
        }
    }

    [TestCase(10, 3, Description = "mid-chunk: one reverse diff then forwards")]
    [TestCase(8, 1, Description = "exactly at a chunk boundary: reverse diff only")]
    [TestCase(11, 4)]
    [TestCase(2, 5, Description = "oldest chunk: all reverse diffs then forwards")]
    public void AssembleHistoricalSnapshots_ReturnsContiguousChainFromPersistedToBase(long baseBlock, int expectedCount)
    {
        BuildArchivedChunks();
        StateId persisted = CreateStateId(12);
        StateId baseState = CreateStateId(baseBlock);

        using SnapshotPooledList assembled = _repository.AssembleHistoricalSnapshots(baseState, persisted, 4);

        Assert.That(assembled.Count, Is.EqualTo(expectedCount));
        AssertContiguousChain(assembled, persisted, baseState);
    }

    [Test]
    public void AssembleHistoricalSnapshots_UnknownOrPrunedState_ReturnsEmpty()
    {
        BuildArchivedChunks();
        _repository.PruneHistory(9, CreateStateId(12));

        using SnapshotPooledList unknownState = _repository.AssembleHistoricalSnapshots(CreateStateId(10, rootByte: 1), CreateStateId(12), 4);
        using SnapshotPooledList prunedState = _repository.AssembleHistoricalSnapshots(CreateStateId(6), CreateStateId(12), 4);

        Assert.That(unknownState.Count, Is.EqualTo(0));
        Assert.That(prunedState.Count, Is.EqualTo(0));
    }

    [TestCase(9, 8, new long[] { 9, 10, 11 }, Description = "inside newest chunk: keep one reverse diff")]
    [TestCase(8, 8, new long[] { 9, 10, 11 }, Description = "at boundary: same keep set")]
    [TestCase(5, 4, new long[] { 5, 6, 7, 9, 10, 11 })]
    [TestCase(-1, 0, new long[] { 1, 2, 3, 5, 6, 7, 9, 10, 11 }, Description = "window beyond oldest: keep everything")]
    public void PruneHistory_KeepsChainDownToChunkBoundaryBelowOldestServed(long oldestServed, long expectedBoundary, long[] keptHistorical)
    {
        BuildArchivedChunks();
        StateId persisted = CreateStateId(12);

        _repository.PruneHistory(oldestServed, persisted);

        using SnapshotPooledList boundaryChain = _repository.AssembleHistoricalSnapshots(CreateStateId(expectedBoundary), persisted, 4);
        using (Assert.EnterMultipleScope())
        {
            for (long block = 1; block < 12; block++)
            {
                bool expected = Array.IndexOf(keptHistorical, block) >= 0;
                Assert.That(_repository.HasHistoricalState(CreateStateId(block)), Is.EqualTo(expected), $"historical {block}");
            }
            Assert.That(boundaryChain.Count, Is.GreaterThan(0), "keep-boundary should stay reachable");
        }

        if (expectedBoundary > 0)
        {
            using SnapshotPooledList belowBoundary = _repository.AssembleHistoricalSnapshots(CreateStateId(expectedBoundary - 4), persisted, 4);
            Assert.That(belowBoundary.Count, Is.EqualTo(0), "chunk below the keep-boundary should be released");
        }
    }

    [Test]
    public void ClearHistory_ReleasesEverything_AndDuplicateReverseDiffIsRejected()
    {
        BuildArchivedChunks();
        Snapshot duplicate = CreateSnapshot(CreateStateId(12), CreateStateId(8));
        bool addedDuplicate = _repository.TryAddReverseDiff(duplicate);
        if (!addedDuplicate) duplicate.Dispose();

        _repository.ClearHistory();

        using SnapshotPooledList assembled = _repository.AssembleHistoricalSnapshots(CreateStateId(8), CreateStateId(12), 4);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(addedDuplicate, Is.False);
            Assert.That(assembled.Count, Is.EqualTo(0));
            for (long block = 1; block < 12; block++)
            {
                Assert.That(_repository.HasHistoricalState(CreateStateId(block)), Is.False, $"historical {block}");
            }
        }
    }
}
