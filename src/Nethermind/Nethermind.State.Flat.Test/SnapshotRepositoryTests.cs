// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Collections.Pooled;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.State.Flat.PersistedSnapshots;
using Nethermind.State.Flat.PersistedSnapshots.Storage;
using Nethermind.State.Flat.Persistence.BloomFilter;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test;

[TestFixture]
public class SnapshotRepositoryTests
{
    private FlatTestContainer _tier = null!;
    private SnapshotRepository _repository = null!;
    private ResourcePool _resourcePool = null!;
    private FlatDbConfig _config = null!;

    [SetUp]
    public void SetUp()
    {
        _config = new FlatDbConfig { CompactSize = 16 };
        _resourcePool = new ResourcePool(_config);
        _tier = new FlatTestContainer();
        _repository = _tier.Repository;
    }

    [TearDown]
    public void TearDown() => _tier.Dispose();

    private StateId CreateStateId(ulong blockNumber, byte rootByte = 0)
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

    private Snapshot AddSnapshotToRepository(ulong fromBlock, ulong toBlock, bool compacted = false, bool withData = false)
        => AddSnapshotToRepository(CreateStateId(fromBlock), CreateStateId(toBlock), compacted, withData);

    private Snapshot AddSnapshotToRepository(StateId from, StateId to, bool compacted = false, bool withData = false)
    {
        Snapshot snapshot = CreateSnapshot(from, to, withData);

        bool added = _repository.TryAdd(snapshot, compacted ? SnapshotTier.InMemoryCompacted : SnapshotTier.InMemoryBase);

        Assert.That(added, Is.True, $"Failed to add snapshot {from}->{to}");

        if (!compacted)
        {
            _repository.AddStateId(to);
        }

        return snapshot;
    }

    private bool TryLease(StateId state, bool compacted, out Snapshot? snapshot)
        => _repository.TryLeaseInMemoryState(state, compacted ? SnapshotTier.InMemoryCompacted : SnapshotTier.InMemoryBase, out snapshot);

    private List<Snapshot> BuildSnapshotChain(ulong startBlock, ulong endBlock)
    {
        List<Snapshot> snapshots = [];
        for (ulong i = startBlock; i < endBlock; i++)
        {
            snapshots.Add(AddSnapshotToRepository(i, i + 1));
        }
        return snapshots;
    }

    private void BuildSnapshotChain(StateId start, ulong endBlock, byte rootByte = 0)
    {
        StateId prev = start;
        for (ulong block = start.BlockNumber + 1; block <= endBlock; block++)
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

        SnapshotTier tier = compacted ? SnapshotTier.InMemoryCompacted : SnapshotTier.InMemoryBase;
        bool added1 = _repository.TryAdd(snapshot1, tier);
        bool added2 = _repository.TryAdd(snapshot2, tier);

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

        _repository.TryAdd(snapshot, SnapshotTier.InMemoryBase);
        bool leasedBefore = _repository.TryLeaseInMemoryState(to, SnapshotTier.InMemoryBase, out Snapshot? leasedSnapshot);
        leasedSnapshot?.Dispose();

        _repository.RemoveAndReleaseInMemoryKnownState(to, SnapshotTier.InMemoryBase);
        bool leasedAfter = _repository.TryLeaseInMemoryState(to, SnapshotTier.InMemoryBase, out _);

        Assert.That(leasedBefore, Is.True);
        Assert.That(leasedAfter, Is.False);
    }

    [Test]
    public void RemoveSnapshot_WithActiveLeases_DisposesWhenAllReleased()
    {
        AddSnapshotToRepository(0, 1);
        StateId to = CreateStateId(1);

        bool leased1 = _repository.TryLeaseInMemoryState(to, SnapshotTier.InMemoryBase, out Snapshot? snapshot1);
        bool leased2 = _repository.TryLeaseInMemoryState(to, SnapshotTier.InMemoryBase, out Snapshot? snapshot2);

        Assert.That(leased1, Is.True);
        Assert.That(leased2, Is.True);

        _repository.RemoveAndReleaseInMemoryKnownState(to, SnapshotTier.InMemoryBase);

        snapshot1!.Dispose();
        snapshot2!.Dispose();

        bool leasedAfter = _repository.TryLeaseInMemoryState(to, SnapshotTier.InMemoryBase, out _);
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
    public void GetStatesUpToBlock_EmptyRepository()
    {
        StateId target = CreateStateId(10);

        ArrayPoolList<StateId> states = _repository.GetStatesUpToBlock(target.BlockNumber);

        Assert.That(states.Count, Is.EqualTo(0));
        states.Dispose();
    }

    [Test]
    public void GetStatesUpToBlock_NoStatesBeforeTarget()
    {
        StateId state10 = CreateStateId(10);
        _repository.AddStateId(state10);

        StateId target = CreateStateId(5);
        ArrayPoolList<StateId> states = _repository.GetStatesUpToBlock(target.BlockNumber);

        Assert.That(states.Count, Is.EqualTo(0));
        states.Dispose();
    }

    [Test]
    public void GetStatesUpToBlock_StatesBeforeTarget()
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
        ArrayPoolList<StateId> states = _repository.GetStatesUpToBlock(target.BlockNumber);

        Assert.That(states.Count, Is.EqualTo(3));
        states.Dispose();
    }

    // ulong.MaxValue is the PreGenesis sentinel ("before any state") — the ulong equivalent of the
    // old negative block number — so GetStatesUpToBlock must return empty for it.
    [TestCase(ulong.MaxValue)]
    public void GetStatesUpToBlock_PreGenesisSentinel_ReturnsEmpty(ulong blockNumber)
    {
        _repository.AddStateId(CreateStateId(1));

        ArrayPoolList<StateId> states = _repository.GetStatesUpToBlock(blockNumber);

        Assert.That(states.Count, Is.EqualTo(0));
        states.Dispose();
    }

    #endregion

    #region AssembleInMemorySnapshotsForCompaction

    [Test]
    public void AssembleInMemorySnapshotsForCompaction_EmptyRepository()
    {
        StateId target = CreateStateId(10);

        using SnapshotPooledList assembled = _repository.AssembleInMemorySnapshotsForCompaction(target, 0, 10);

        Assert.That(assembled.Count, Is.EqualTo(0));
    }

    [Test]
    public void AssembleInMemorySnapshotsForCompaction_SingleSnapshot()
    {
        AddSnapshotToRepository(0, 1);

        StateId target = CreateStateId(1);
        using SnapshotPooledList assembled = _repository.AssembleInMemorySnapshotsForCompaction(target, 0, 10);

        Assert.That(assembled.Count, Is.EqualTo(1));
        Assert.That(assembled[0].To, Is.EqualTo(target));
    }

    [Test]
    public void AssembleInMemorySnapshotsForCompaction_LinearChain()
    {
        BuildSnapshotChain(0, 4);

        StateId target = CreateStateId(4);
        using SnapshotPooledList assembled = _repository.AssembleInMemorySnapshotsForCompaction(target, 0, 10);

        Assert.That(assembled.Count, Is.EqualTo(4));
    }

    [Test]
    public void AssembleInMemorySnapshotsForCompaction_StopsAtStartingBlock()
    {
        BuildSnapshotChain(0, 5);

        StateId target = CreateStateId(4);
        using SnapshotPooledList assembled = _repository.AssembleInMemorySnapshotsForCompaction(target, 2, 10);

        Assert.That(assembled.Count, Is.EqualTo(2));
    }

    [Test]
    public void AssembleInMemorySnapshotsForCompaction_PrefersCompacted()
    {
        StateId from = CreateStateId(0);
        StateId to = CreateStateId(1);

        Snapshot compacted = CreateSnapshot(from, to);
        _repository.TryAdd(compacted, SnapshotTier.InMemoryCompacted);

        using SnapshotPooledList assembled = _repository.AssembleInMemorySnapshotsForCompaction(to, 0, 10);

        Assert.That(assembled.Count, Is.EqualTo(1));
    }

    #endregion

    #region AssembleSnapshots

    [Test]
    public void AssembleSnapshots_PersistedSpanning_BelowTarget_AcceptedAsTerminal()
    {
        StateId s0 = CreateStateId(0);
        StateId s2 = CreateStateId(2);
        StateId s5 = CreateStateId(5);

        // A persisted base spanning (s0, s5] — its From is below the target s2.
        _tier.ConvertToPersistedBase(CreateSnapshot(s0, s5)).Dispose();

        using AssembledSnapshotResult result = _repository.AssembleSnapshots(s5, s2, 4);

        Assert.That(result.Persisted.Count, Is.EqualTo(1));
        Assert.That(result.InMemory.Count, Is.EqualTo(0));
        Assert.That(result.Persisted[0].From.BlockNumber, Is.LessThan(s2.BlockNumber));
    }

    [Test]
    public void AssembleSnapshots_InMemoryOvershoot_Rejected()
    {
        StateId s2 = CreateStateId(2);
        StateId s5 = CreateStateId(5);

        AddSnapshotToRepository(0, 5, compacted: true);

        using AssembledSnapshotResult result = _repository.AssembleSnapshots(s5, s2, 4);

        Assert.That(result.SnapshotCount, Is.EqualTo(0));
    }

    [Test]
    public void AssembleSnapshots_ExactPersistedMatch_AcceptedAsWinner()
    {
        StateId s2 = CreateStateId(2);
        StateId s5 = CreateStateId(5);

        // A persisted base whose From is exactly the target s2.
        _tier.ConvertToPersistedBase(CreateSnapshot(s2, s5)).Dispose();

        using AssembledSnapshotResult result = _repository.AssembleSnapshots(s5, s2, 4);

        Assert.That(result.Persisted.Count, Is.EqualTo(1));
        Assert.That(result.InMemory.Count, Is.EqualTo(0));
        Assert.That(result.Persisted[0].From.BlockNumber, Is.EqualTo(s2.BlockNumber));
    }

    [Test]
    public void AssembleSnapshots_LinearChain_ReturnsAscendingPathToTarget()
    {
        BuildSnapshotChain(0, 5);

        using AssembledSnapshotResult assembled = _repository.AssembleSnapshots(CreateStateId(5), CreateStateId(0), 10);

        Assert.That(assembled.InMemory.Count, Is.EqualTo(5));
        Assert.That(assembled.InMemory[0].From, Is.EqualTo(CreateStateId(0)));
        Assert.That(assembled.InMemory[^1].To, Is.EqualTo(CreateStateId(5)));
    }

    [Test]
    public void AssembleSnapshots_CompactedSnapshot_TakesWideHop()
    {
        AddSnapshotToRepository(0, 5, compacted: true);

        using AssembledSnapshotResult assembled = _repository.AssembleSnapshots(CreateStateId(5), CreateStateId(0), 10);

        Assert.That(assembled.InMemory.Count, Is.EqualTo(1));
        Assert.That(assembled.InMemory[0].From, Is.EqualTo(CreateStateId(0)));
        Assert.That(assembled.InMemory[0].To, Is.EqualTo(CreateStateId(5)));
    }

    [Test]
    public void AssembleSnapshots_CompactedOvershoot_FallsBackToBaseEdges()
    {
        BuildSnapshotChain(0, 5);
        AddSnapshotToRepository(0, 5, compacted: true);

        using AssembledSnapshotResult assembled = _repository.AssembleSnapshots(CreateStateId(5), CreateStateId(2), 10);

        Assert.That(assembled.InMemory.Count, Is.EqualTo(3));
        Assert.That(assembled.InMemory[0].From, Is.EqualTo(CreateStateId(2)));
        Assert.That(assembled.InMemory[^1].To, Is.EqualTo(CreateStateId(5)));
    }

    [Test]
    public void AssembleSnapshots_BaseEqualsTarget_ReturnsEmpty()
    {
        BuildSnapshotChain(0, 3);

        using AssembledSnapshotResult assembled = _repository.AssembleSnapshots(CreateStateId(3), CreateStateId(3), 10);

        Assert.That(assembled.InMemory.Count, Is.EqualTo(0));
    }

    [Test]
    public void AssembleSnapshots_UnreachableTarget_ReturnsEmpty()
    {
        BuildSnapshotChain(1, 4);

        using AssembledSnapshotResult assembled = _repository.AssembleSnapshots(CreateStateId(4), CreateStateId(0), 10);

        Assert.That(assembled.InMemory.Count, Is.EqualTo(0));
    }

    [Test]
    public void AssembleSnapshots_SelfReferencingSnapshot_ReturnsEmptyWithoutHanging()
    {
        AddSnapshotToRepository(CreateStateId(1), CreateStateId(1));

        using AssembledSnapshotResult assembled = _repository.AssembleSnapshots(CreateStateId(1), CreateStateId(0), 10);

        Assert.That(assembled.InMemory.Count, Is.EqualTo(0));
    }

    #endregion

    #region RemoveSiblingAndDescendents

    [Test]
    public void RemoveSiblingAndDescendents_LinearChain_RemovesNothing()
    {
        BuildSnapshotChain(0, 10);

        _repository.RemoveSiblingAndDescendents(CreateStateId(5));

        for (ulong block = 1; block <= 10; block++)
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

    [Test]
    public void RemoveOrphanedStates_PinnedParent_DropsSiblingsOffTheCommittedAncestry()
    {
        // Canonical 0..3, then three sibling pairs (4, 5) under block 3; only the last pair was committed.
        BuildSnapshotChain(0, 3);
        for (byte fork = 1; fork <= 3; fork++)
        {
            AddSnapshotToRepository(CreateStateId(3), CreateStateId(4, fork));
            AddSnapshotToRepository(CreateStateId(4, fork), CreateStateId(5, fork));
        }
        _repository.SetLastCommittedStateId(CreateStateId(5, rootByte: 3));

        int pruned = _repository.RemoveOrphanedStates(CreateStateId(5, rootByte: 3), CreateStateId(5, rootByte: 3));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(pruned, Is.EqualTo(4));
            Assert.That(_repository.HasState(CreateStateId(4, rootByte: 1)), Is.False);
            Assert.That(_repository.HasState(CreateStateId(5, rootByte: 2)), Is.False);
            Assert.That(_repository.HasState(CreateStateId(4, rootByte: 3)), Is.True, "the committed pair stays");
            Assert.That(_repository.HasState(CreateStateId(5, rootByte: 3)), Is.True);
            Assert.That(_repository.HasState(CreateStateId(1)), Is.True, "canonical ancestry stays");
            Assert.That(_repository.HasState(CreateStateId(3)), Is.True);
        }
    }

    [Test]
    public void RemoveOrphanedStates_RecentlyCommittedSibling_KeepsItsAncestry([Values(2, 17, 48, 80)] int commits)
    {
        BuildSnapshotChain(0, 3);
        for (byte fork = 1; fork <= commits; fork++)
        {
            AddSnapshotToRepository(CreateStateId(3), CreateStateId(4, fork));
            AddSnapshotToRepository(CreateStateId(4, fork), CreateStateId(5, fork));
            _repository.SetLastCommittedStateId(CreateStateId(5, fork));
        }

        int pruned = _repository.RemoveOrphanedStates(CreateStateId(5, (byte)commits), CreateStateId(5, (byte)commits));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(pruned, Is.EqualTo(commits > 16 ? 2 * (commits - 16) : 0));
            for (byte fork = 1; fork <= commits; fork++)
            {
                bool retained = fork > commits - 16;
                Assert.That(_repository.HasState(CreateStateId(4, fork)), Is.EqualTo(retained), $"parent of committed sibling {fork}");
                Assert.That(_repository.HasState(CreateStateId(5, fork)), Is.EqualTo(retained), $"committed sibling {fork}");
            }
        }
    }

    [Test]
    public void RemoveOrphanedStates_CompactedEdge_KeepsTheBasesItSpans()
    {
        BuildSnapshotChain(0, 4);
        AddSnapshotToRepository(CreateStateId(0), CreateStateId(3), compacted: true);
        AddSnapshotToRepository(CreateStateId(1), CreateStateId(2, rootByte: 1));
        _repository.SetLastCommittedStateId(CreateStateId(4));

        int pruned = _repository.RemoveOrphanedStates(CreateStateId(4), CreateStateId(4));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(pruned, Is.EqualTo(1));
            Assert.That(_repository.HasState(CreateStateId(2, rootByte: 1)), Is.False);
            Assert.That(_repository.HasState(CreateStateId(1)), Is.True);
            Assert.That(_repository.HasState(CreateStateId(2)), Is.True);
            Assert.That(TryLease(CreateStateId(3), compacted: true, out Snapshot? compacted), Is.True);
            compacted?.Dispose();
        }
    }

    [Test]
    public void CollectCommittedAncestry_DistinguishesTheCommittedChainFromOrphans()
    {
        BuildSnapshotChain(0, 4);
        AddSnapshotToRepository(CreateStateId(2), CreateStateId(3, rootByte: 1));

        using PooledSet<StateId> retained = new();
        _repository.CollectCommittedAncestry(CreateStateId(3, rootByte: 1), retained);
        Assert.That(retained, Is.Empty, "nothing committed yet: ancestry is unknown");

        _repository.SetLastCommittedStateId(CreateStateId(4));
        _repository.CollectCommittedAncestry(CreateStateId(4), retained);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(retained.Contains(CreateStateId(2)), Is.True);
            Assert.That(retained.Contains(CreateStateId(4)), Is.True);
            Assert.That(retained.Contains(CreateStateId(3, rootByte: 1)), Is.False);
        }
    }

    [Test]
    public void RemoveOrphanedStates_ConvertedSibling_IsDroppedWithOrWithoutInMemoryOrphans([Values] bool inMemoryOrphan)
    {
        BuildSnapshotChain(0, 3);
        using Snapshot orphan = CreateSnapshot(CreateStateId(2), CreateStateId(3, rootByte: 1), withData: true);
        _tier.ConvertToPersistedBase(orphan).Dispose();
        if (inMemoryOrphan) AddSnapshotToRepository(CreateStateId(2), CreateStateId(3, rootByte: 2));
        _repository.SetLastCommittedStateId(CreateStateId(3));
        Assert.That(_repository.HasState(CreateStateId(3, rootByte: 1)), Is.True, "precondition: the sibling lives in the persisted tier");

        int pruned = _repository.RemoveOrphanedStates(CreateStateId(3), CreateStateId(3));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(pruned, Is.EqualTo(inMemoryOrphan ? 2 : 1));
            Assert.That(_repository.HasState(CreateStateId(3, rootByte: 1)), Is.False);
            Assert.That(_repository.HasState(CreateStateId(3)), Is.True);
        }
    }

    [Test]
    public void RemoveOrphanedStates_PreservesHistorySkippedByCompaction([Values] bool insideWindow)
    {
        using Snapshot oldBase = CreateSnapshot(CreateStateId(0), CreateStateId(1), withData: true);
        using Snapshot compacted = CreateSnapshot(CreateStateId(0), CreateStateId(2), withData: true);
        _tier.ConvertToPersistedBase(oldBase).Dispose();
        _tier.ConvertToPersistedBase(compacted).Dispose();
        AddSnapshotToRepository(CreateStateId(2), CreateStateId(3));
        AddSnapshotToRepository(CreateStateId(2), CreateStateId(3, rootByte: 1));
        if (insideWindow) AddSnapshotToRepository(CreateStateId(0), CreateStateId(1, rootByte: 1));
        _repository.SetLastCommittedStateId(CreateStateId(3));

        int pruned = _repository.RemoveOrphanedStates(CreateStateId(3), CreateStateId(3));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(pruned, Is.EqualTo(1));
            Assert.That(_repository.HasState(CreateStateId(3, rootByte: 1)), Is.False);
            Assert.That(_repository.HasState(CreateStateId(1)), Is.True, "compacted ancestry cannot classify skipped history as orphaned");
            Assert.That(_repository.HasState(CreateStateId(2)), Is.True);
            if (insideWindow) Assert.That(_repository.HasState(CreateStateId(1, rootByte: 1)), Is.True);
        }
    }

    [Test]
    public void RemoveOrphanedStates_AnotherProtectedBranchDoesNotClassifySkippedCanonicalHistory()
    {
        StateId start = CreateStateId(0);
        StateId canonicalInterior = CreateStateId(5);
        StateId compactedTip = CreateStateId(10);
        StateId otherBranch = CreateStateId(5, 1);
        StateId head = CreateStateId(11);
        using Snapshot interior = CreateSnapshot(start, canonicalInterior, withData: true);
        using Snapshot compacted = CreateSnapshot(start, compactedTip, withData: true);
        using Snapshot competing = CreateSnapshot(start, otherBranch, withData: true);
        _tier.ConvertToPersistedBase(interior).Dispose();
        _tier.ConvertToPersistedBase(compacted).Dispose();
        _tier.ConvertToPersistedBase(competing).Dispose();
        AddSnapshotToRepository(compactedTip, head);
        AddSnapshotToRepository(compactedTip, CreateStateId(11, 1));
        _repository.SetLastCommittedStateId(otherBranch);
        _repository.SetLastCommittedStateId(head);

        int pruned = _repository.RemoveOrphanedStates(head, head);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(pruned, Is.EqualTo(1));
            Assert.That(_repository.HasState(canonicalInterior), Is.True);
            Assert.That(_repository.HasState(otherBranch), Is.True);
            Assert.That(_repository.HasState(CreateStateId(11, 1)), Is.False);
        }
    }

    [Test]
    public void RemoveOrphanedStates_RemovesDescendantsWithoutAnAlternativeParent([Values] bool alternativeParent, [Values] bool childInMemory)
    {
        StateId start = CreateStateId(4);
        StateId parent = CreateStateId(5);
        StateId head = CreateStateId(8);
        StateId orphanParent = CreateStateId(5, 1);
        StateId orphanChild = CreateStateId(6, 1);
        StateId orphanGrandchild = CreateStateId(7, 1);
        using Snapshot canonicalParent = CreateSnapshot(start, parent, withData: true);
        using Snapshot canonicalInterior = CreateSnapshot(parent, CreateStateId(6), withData: true);
        using Snapshot competingParent = CreateSnapshot(start, orphanParent, withData: true);
        using Snapshot competingChild = CreateSnapshot(orphanParent, orphanChild, withData: true);
        using Snapshot competingGrandchild = CreateSnapshot(orphanChild, orphanGrandchild, withData: true);
        _tier.ConvertToPersistedBase(canonicalParent).Dispose();
        _tier.ConvertToPersistedBase(canonicalInterior).Dispose();
        _tier.ConvertToPersistedBase(competingParent).Dispose();
        using PersistedSnapshot child = _tier.ConvertToPersistedBase(competingChild);
        _tier.ConvertToPersistedBase(competingGrandchild).Dispose();
        if (childInMemory)
        {
            _repository.RemovePersistedStateExact(orphanChild);
            AddSnapshotToRepository(orphanParent, orphanChild);
        }
        if (alternativeParent)
        {
            using PersistedSnapshot alternative = new(parent, orphanChild, child.Reservation, _tier.Blobs,
                SnapshotTier.PersistedSmallCompacted, RefCountedBloomFilter.AlwaysTrue());
            _repository.AddPersistedSnapshot(alternative, SnapshotTier.PersistedSmallCompacted);
        }
        AddSnapshotToRepository(parent, head);
        AddSnapshotToRepository(start, CreateStateId(5, 2));
        _repository.SetLastCommittedStateId(head);

        int pruned = _repository.RemoveOrphanedStates(head, head);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(pruned, Is.EqualTo(alternativeParent ? 2 : 4));
            Assert.That(_repository.HasState(orphanParent), Is.False);
            Assert.That(_repository.HasState(orphanChild), Is.EqualTo(alternativeParent));
            Assert.That(_repository.HasState(orphanGrandchild), Is.EqualTo(alternativeParent));
            Assert.That(_repository.HasState(CreateStateId(6)), Is.True);
        }
        if (alternativeParent)
        {
            using AssembledSnapshotResult assembled = _repository.AssembleSnapshots(orphanChild, start, 2);
            Assert.That(assembled.SnapshotCount, Is.EqualTo(2));
        }
    }

    [Test]
    public async Task RemoveOrphanedStates_CleanupDoesNotBlockRegistrationOrCommit([Values] bool cleanupThrows)
    {
        StateId head = CreateStateId(1);
        StateId orphan = CreateStateId(1, 1);
        StateId anotherOrphan = CreateStateId(1, 2);
        AddSnapshotToRepository(CreateStateId(0), head);
        _repository.SetLastCommittedStateId(head);
        TaskCompletionSource cleanupStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseCleanup = new(TaskCreationOptions.RunContinuationsAsynchronously);
        bool otherCleaned = false;
        Snapshot blocked = new CleanupSnapshot(CreateStateId(0), orphan, _resourcePool, () =>
        {
            cleanupStarted.TrySetResult();
            releaseCleanup.Task.GetAwaiter().GetResult();
            if (cleanupThrows) throw new InvalidOperationException("Cleanup failed");
        });
        Snapshot other = new CleanupSnapshot(CreateStateId(0), anotherOrphan, _resourcePool, () => otherCleaned = true);
        Assert.That(_repository.TryAdd(blocked, SnapshotTier.InMemoryBase), Is.True);
        _repository.AddStateId(orphan);
        Assert.That(_repository.TryAdd(other, SnapshotTier.InMemoryBase), Is.True);
        _repository.AddStateId(anotherOrphan);

        Task<int> pruning = Task.Run(() => _repository.RemoveOrphanedStates(head, head));
        try
        {
            await cleanupStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Task publish = Task.Run(() =>
            {
                SnapshotRetention retention = _tier.Resolve<SnapshotRetention>();
                using Lock.Scope scope = retention.Sync.EnterScope();
                retention.Register(head);
                try
                {
                    Assert.That(_repository.HasState(orphan), Is.False);
                    _repository.SetLastCommittedStateId(head);
                }
                finally { retention.Release(head); }
            });
            await publish.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            releaseCleanup.TrySetResult();
            if (cleanupThrows)
                await Assert.ThatAsync(async () => await pruning, Throws.TypeOf<AggregateException>());
            else
                Assert.That(await pruning, Is.EqualTo(2));
        }
        Assert.That(otherCleaned, Is.True, "all detached leases must be released even when one cleanup throws");
    }

    [Test]
    public void RemoveOrphanedStates_PreservesPrimaryFailure([Values] bool cleanupThrows)
    {
        ISnapshotCatalog catalog = Substitute.For<ISnapshotCatalog>();
        using FlatTestContainer tier = new(configure: builder => builder.AddSingleton(catalog));
        SnapshotRepository repository = tier.Repository;
        StateId start = CreateStateId(0);
        StateId head = CreateStateId(1);
        StateId orphan = CreateStateId(1, 1);
        StateId persistedOrphan = CreateStateId(1, 2);
        InvalidOperationException primary = new("Catalog removal failed");
        InvalidOperationException cleanup = new("Snapshot cleanup failed");
        bool cleaned = false;
        Snapshot snapshot = new CleanupSnapshot(start, orphan, tier.ResourcePool, () =>
        {
            cleaned = true;
            if (cleanupThrows) throw cleanup;
        });
        Assert.That(repository.TryAdd(snapshot, SnapshotTier.InMemoryBase), Is.True);
        repository.AddStateId(orphan);
        Assert.That(repository.TryAdd(tier.ResourcePool.CreateSnapshot(start, head, ResourcePool.Usage.ReadOnlyProcessingEnv), SnapshotTier.InMemoryBase), Is.True);
        repository.AddStateId(head);
        repository.SetLastCommittedStateId(head);
        using Snapshot persisted = tier.ResourcePool.CreateSnapshot(start, persistedOrphan, ResourcePool.Usage.ReadOnlyProcessingEnv);
        tier.ConvertToPersistedBase(persisted).Dispose();
        catalog.Remove(persistedOrphan, 1).Returns(_ => throw primary);

        Exception? failure = Assert.Catch(() => repository.RemoveOrphanedStates(head, head));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cleaned, Is.True);
            if (cleanupThrows)
                Assert.That((failure as AggregateException)?.InnerExceptions, Is.EqualTo(new Exception[] { primary, cleanup }));
            else
                Assert.That(failure, Is.SameAs(primary));
        }
    }

    [Test]
    public void RemoveOrphanedStates_ReportsCandidatesPreservedByCompactionGaps([Values] bool hasOrphan)
    {
        InterfaceLogger logger = Substitute.For<InterfaceLogger>();
        logger.IsDebug.Returns(true);
        ILogManager logs = Substitute.For<ILogManager>();
        ILogger wrappedLogger = new(logger);
        logs.GetClassLogger<SnapshotRepository>().Returns(wrappedLogger);
        using FlatTestContainer tier = new(configure: builder => builder.AddSingleton(logs));
        SnapshotRepository repository = tier.Repository;
        StateId start = CreateStateId(0);
        StateId head = CreateStateId(2);
        StateId interior = CreateStateId(1);
        foreach (StateId to in new[] { head, interior })
        {
            Assert.That(repository.TryAdd(tier.ResourcePool.CreateSnapshot(start, to, ResourcePool.Usage.ReadOnlyProcessingEnv), SnapshotTier.InMemoryBase), Is.True);
            repository.AddStateId(to);
        }
        if (hasOrphan)
        {
            StateId orphan = CreateStateId(2, 1);
            Assert.That(repository.TryAdd(tier.ResourcePool.CreateSnapshot(start, orphan, ResourcePool.Usage.ReadOnlyProcessingEnv), SnapshotTier.InMemoryBase), Is.True);
            repository.AddStateId(orphan);
        }
        repository.SetLastCommittedStateId(head);

        int pruned = repository.RemoveOrphanedStates(head, head);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(pruned, Is.EqualTo(hasOrphan ? 1 : 0));
            Assert.That(repository.HasState(interior), Is.True);
            logger.Received().Debug(Arg.Is<string>(message => message.Contains("Preserved 1 snapshot candidate entries") && message.Contains("compaction gaps")));
        }
    }

    private sealed class CleanupSnapshot(StateId from, StateId to, IResourcePool pool, Action cleanup)
        : Snapshot(from, to, new SnapshotContent(), pool, ResourcePool.Usage.ReadOnlyProcessingEnv)
    {
        protected override void CleanUp()
        {
            try { cleanup(); }
            finally { base.CleanUp(); }
        }
    }

    [Test]
    public void RemoveOrphanedStates_UnknownProtectedAncestry_RateLimitsWarnings([Values] bool missingCommittedHead)
    {
        InterfaceLogger logger = Substitute.For<InterfaceLogger>();
        logger.IsWarn.Returns(true);
        ILogManager logs = Substitute.For<ILogManager>();
        ILogger wrappedLogger = new(logger);
        logs.GetClassLogger<SnapshotRepository>().Returns(wrappedLogger);
        using FlatTestContainer tier = new(configure: builder => builder.AddSingleton(logs));
        SnapshotRepository repository = tier.Repository;
        StateId head = CreateStateId(2);
        Assert.That(repository.TryAdd(tier.ResourcePool.CreateSnapshot(CreateStateId(1), head, ResourcePool.Usage.ReadOnlyProcessingEnv), SnapshotTier.InMemoryBase), Is.True);
        repository.AddStateId(head);
        StateId committed = missingCommittedHead ? CreateStateId(3) : head;
        StateId start = CreateStateId(0);
        Assert.That(repository.TryAdd(tier.ResourcePool.CreateSnapshot(StateId.PreGenesis, start, ResourcePool.Usage.ReadOnlyProcessingEnv), SnapshotTier.InMemoryBase), Is.True);
        repository.AddStateId(start);
        long skipped = Metrics.SnapshotOrphanPruningSkipped;

        Assert.That(repository.RemoveOrphanedStates(committed, committed), Is.Zero);
        Assert.That(repository.RemoveOrphanedStates(committed, committed), Is.Zero);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(repository.HasState(head), Is.True);
            Assert.That(Metrics.SnapshotOrphanPruningSkipped, Is.GreaterThanOrEqualTo(skipped + 2));
            logger.Received(1).Warn(Arg.Is<string>(message => message.Contains("Skipped snapshot orphan pruning") && message.Contains("protected ancestry")));
        }
    }

    [Test]
    public void RemoveOrphanedStates_AncestryGapAboveTheLowerBound_PrunesNothing()
    {
        AddSnapshotToRepository(CreateStateId(0), CreateStateId(1));
        AddSnapshotToRepository(CreateStateId(2), CreateStateId(3));
        AddSnapshotToRepository(CreateStateId(2), CreateStateId(3, rootByte: 1));
        _repository.SetLastCommittedStateId(CreateStateId(3));

        int pruned = _repository.RemoveOrphanedStates(CreateStateId(3), CreateStateId(3));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(pruned, Is.Zero);
            Assert.That(_repository.HasState(CreateStateId(1)), Is.True);
            Assert.That(_repository.HasState(CreateStateId(3)), Is.True);
            Assert.That(_repository.HasState(CreateStateId(3, rootByte: 1)), Is.True, "incomplete ancestry cannot prove a sibling is orphaned");
        }
    }

    [Test]
    public void RemoveOrphanedStates_ForkChoiceHead_IsRetainedWithItsAncestry()
    {
        // The chain follows 4 while every later payload is a sibling of it that was executed but never selected.
        BuildSnapshotChain(0, 4);
        for (byte fork = 1; fork <= 3; fork++)
        {
            AddSnapshotToRepository(CreateStateId(3), CreateStateId(4, fork));
            _repository.SetLastCommittedStateId(CreateStateId(4, fork));
        }

        int pruned = _repository.RemoveOrphanedStates(CreateStateId(4, rootByte: 3), CreateStateId(4));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(pruned, Is.Zero, "recently committed siblings and the fork-choice head all stay");
            Assert.That(_repository.HasState(CreateStateId(4)), Is.True, "the fork-choice head is not an orphan");
        }
    }

    [Test]
    public void RemoveOrphanedStates_ForkAboveTheCommittedHead_IsDroppedWhole()
    {
        // An orphaned fork 3'->4'->5' reaches above the committed head at 4; keeping 5' would leave a tip whose chain
        // cannot be assembled.
        BuildSnapshotChain(0, 4);
        BuildSnapshotChain(CreateStateId(2), 5, rootByte: 1);
        _repository.SetLastCommittedStateId(CreateStateId(4));

        int pruned = _repository.RemoveOrphanedStates(CreateStateId(4), CreateStateId(4));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(pruned, Is.EqualTo(3));
            Assert.That(_repository.HasState(CreateStateId(5, rootByte: 1)), Is.False);
            Assert.That(_repository.HasState(CreateStateId(3, rootByte: 1)), Is.False);
            Assert.That(_repository.HasState(CreateStateId(4)), Is.True);
        }
    }

    [Test]
    public void RemoveOrphanedStates_HeadWithoutInMemorySnapshot_PrunesNothing()
    {
        BuildSnapshotChain(0, 3);
        AddSnapshotToRepository(CreateStateId(1), CreateStateId(2, rootByte: 1));

        Assert.That(_repository.RemoveOrphanedStates(CreateStateId(9), CreateStateId(9)), Is.Zero);
        Assert.That(_repository.HasState(CreateStateId(2, rootByte: 1)), Is.True);
    }

    #endregion
}
