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
    {
        StateId from = CreateStateId(fromBlock);
        StateId to = CreateStateId(toBlock);
        Snapshot snapshot = CreateSnapshot(from, to, withData);

        bool added = compacted
            ? _repository.TryAddCompactedSnapshot(snapshot)
            : _repository.TryAddSnapshot(snapshot);

        Assert.That(added, Is.True, $"Failed to add snapshot {fromBlock}->{toBlock}");

        if (!compacted)
        {
            _repository.AddStateId(to);
        }

        return snapshot;
    }

    private List<Snapshot> BuildSnapshotChain(long startBlock, long endBlock)
    {
        List<Snapshot> snapshots = new List<Snapshot>();
        for (long i = startBlock; i < endBlock; i++)
        {
            snapshots.Add(AddSnapshotToRepository(i, i + 1));
        }
        return snapshots;
    }

    #region Snapshot Addition and Removal

    [Test]
    public void TryAddSnapshot_NewAndDuplicate_BehavesCorrectly()
    {
        StateId from = CreateStateId(0);
        StateId to = CreateStateId(1);
        Snapshot snapshot1 = CreateSnapshot(from, to);
        Snapshot snapshot2 = CreateSnapshot(from, to);

        bool added1 = _repository.TryAddSnapshot(snapshot1);
        bool added2 = _repository.TryAddSnapshot(snapshot2);

        Assert.That(added1, Is.True);
        Assert.That(added2, Is.False);

        snapshot2.Dispose();
    }

    [Test]
    public void TryAddCompactedSnapshot_NewAndDuplicate_BehavesCorrectly()
    {
        StateId from = CreateStateId(0);
        StateId to = CreateStateId(1);
        Snapshot snapshot1 = CreateSnapshot(from, to);
        Snapshot snapshot2 = CreateSnapshot(from, to);

        bool added1 = _repository.TryAddCompactedSnapshot(snapshot1);
        bool added2 = _repository.TryAddCompactedSnapshot(snapshot2);

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
    public void TryLeaseState_ExistingAndNonExistent()
    {
        AddSnapshotToRepository(0, 1);

        StateId existing = CreateStateId(1);
        bool leasedExisting = _repository.TryLeaseState(existing, out Snapshot? snapshot);
        Assert.That(leasedExisting, Is.True);
        Assert.That(snapshot, Is.Not.Null);
        snapshot!.Dispose();

        StateId nonExistent = CreateStateId(999);
        bool leasedNonExistent = _repository.TryLeaseState(nonExistent, out Snapshot? nonExistentSnapshot);
        Assert.That(leasedNonExistent, Is.False);
        Assert.That(nonExistentSnapshot, Is.Null);
    }

    [Test]
    public void TryLeaseState_MultipleLeases_AllSucceed()
    {
        AddSnapshotToRepository(0, 1);

        StateId to = CreateStateId(1);
        bool leased1 = _repository.TryLeaseState(to, out Snapshot? snapshot1);
        bool leased2 = _repository.TryLeaseState(to, out Snapshot? snapshot2);
        bool leased3 = _repository.TryLeaseState(to, out Snapshot? snapshot3);

        Assert.That(leased1, Is.True);
        Assert.That(leased2, Is.True);
        Assert.That(leased3, Is.True);

        Assert.That(snapshot1, Is.SameAs(snapshot2));
        Assert.That(snapshot2, Is.SameAs(snapshot3));

        snapshot1!.Dispose();
        snapshot2!.Dispose();
        snapshot3!.Dispose();
    }

    [Test]
    public void TryLeaseCompactedState_ExistingAndNonExistent()
    {
        AddSnapshotToRepository(0, 1, compacted: true);

        StateId existing = CreateStateId(1);
        bool leasedExisting = _repository.TryLeaseCompactedState(existing, out Snapshot? snapshot);
        Assert.That(leasedExisting, Is.True);
        Assert.That(snapshot, Is.Not.Null);
        snapshot!.Dispose();

        StateId nonExistent = CreateStateId(999);
        bool leasedNonExistent = _repository.TryLeaseCompactedState(nonExistent, out Snapshot? nonExistentSnapshot);
        Assert.That(leasedNonExistent, Is.False);
        Assert.That(nonExistentSnapshot, Is.Null);
    }

    [Test]
    public void TryLeaseCompactedState_MultipleLeases_AllSucceed()
    {
        AddSnapshotToRepository(0, 1, compacted: true);

        StateId to = CreateStateId(1);
        bool leased1 = _repository.TryLeaseCompactedState(to, out Snapshot? snapshot1);
        bool leased2 = _repository.TryLeaseCompactedState(to, out Snapshot? snapshot2);

        Assert.That(leased1, Is.True);
        Assert.That(leased2, Is.True);

        snapshot1!.Dispose();
        snapshot2!.Dispose();
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
}
