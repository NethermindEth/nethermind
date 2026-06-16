// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
}
