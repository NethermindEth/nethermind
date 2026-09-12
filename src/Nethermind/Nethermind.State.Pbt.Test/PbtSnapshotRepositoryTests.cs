// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Logging;
using NUnit.Framework;

namespace Nethermind.State.Pbt.Test;

public class PbtSnapshotRepositoryTests
{
    private readonly PbtResourcePool _pool = new(new PbtConfig());
    private PbtSnapshotRepository _repository = null!;

    [SetUp]
    public void SetUp() => _repository = new();

    [TearDown]
    public void TearDown() => _repository.RemoveStatesUntil(ulong.MaxValue);

    [Test]
    public void DuplicateBaseSnapshot_IsRejectedWithoutMovingCommittedHead()
    {
        Add(StateId.PreGenesis, State(0));
        Add(State(0), State(1));
        Assert.That(_repository.TryAdd(Snapshot(StateId.PreGenesis, State(0))), Is.False);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(_repository.GetLastCommittedStateId(), Is.EqualTo(State(1)));
            Assert.That(_repository.Count, Is.EqualTo(2));
        }
    }

    [TestCase(0, 64, 32, 32)]
    [TestCase(32, 64, 32, 64)]
    [TestCase(0, 64, 0, 1)]
    [TestCase(0, 64, 8, 8)]
    [TestCase(3, 64, 32, 4)]
    [TestCase(32, 35, 32, 33)]
    [TestCase(-1, 64, 32, 0)]
    public void FindSnapshotToPersist_PrefersFullUnitsWithSmallerAndBaseFallback(int floor, int head, int compactedWidth, int expected)
    {
        for (int block = 0; block <= head; block++) Add(State(block - 1), State(block));
        if (compactedWidth > 0)
            for (int block = compactedWidth; block <= head; block += compactedWidth)
                Add(State(block - compactedWidth), State(block), compacted: true);

        using PbtSnapshot? candidate = _repository.FindSnapshotToPersist(State(head), State(floor), 32);
        Assert.That(candidate, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(candidate!.From, Is.EqualTo(State(floor)));
            Assert.That(candidate.To, Is.EqualTo(State(expected)));
            Assert.That(candidate.To.BlockNumber - candidate.From.BlockNumber, Is.LessThanOrEqualTo(32UL));
        }
    }

    [Test]
    public void FindSnapshotToPersist_RejectsBrokenOrWrongRootChains([Values] bool wrongRoot)
    {
        StateId from = wrongRoot ? new StateId(0, TestItem.KeccakA.ValueHash256) : State(1);
        Add(from, State(2));
        using PbtSnapshot? candidate = _repository.FindSnapshotToPersist(State(2), State(0), 32);
        Assert.That(candidate, Is.Null);
    }

    [Test]
    public void FindSnapshotToPersist_RejectedWideEdgeDoesNotHideBaseCandidate()
    {
        Add(State(0), State(1));
        Add(State(1), State(2));
        Add(State(0), State(2), compacted: true);
        using PbtSnapshot? candidate = _repository.FindSnapshotToPersist(State(2), State(0), 1);
        Assert.That(candidate?.To, Is.EqualTo(State(1)));
    }

    [Test]
    public void Compaction_RetainsFullBoundariesAndRetiresOlderPartialUnits([Values(0, 3)] int offset)
    {
        PbtConfig config = new() { CompactSize = 8, CompactionOffset = offset };
        using MemDb metadata = new();
        PbtSnapshotCompactor compactor = new(_pool, new PbtCompactionSchedule(metadata, config, LimboLogs.Instance), _repository, config);
        int firstBoundary = 8 - offset;
        for (int block = 0; block <= firstBoundary + 18; block++)
        {
            Add(State(block - 1), State(block));
            compactor.DoCompactSnapshot(State(block));
        }

        int secondBoundary = firstBoundary + 8;
        using PbtSnapshotPooledList full = new(1);
        using PbtSnapshotPooledList partial = new(1);
        Assert.That(_repository.TryLeaseCompactionWindow(State(secondBoundary), firstBoundary, full), Is.True);
        Assert.That(_repository.TryLeaseCompactionWindow(State(secondBoundary + 2), secondBoundary, partial), Is.True);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(full.Count, Is.EqualTo(1));
            Assert.That(partial.Count, Is.EqualTo(2));
        }
    }

    [Test]
    public void Pruning_RemovesOrphanDescendantsButPreservesCanonicalForksAndReaderLeases()
    {
        StateId orphan = new(1, TestItem.KeccakA.ValueHash256);
        StateId orphanChild = new(2, TestItem.KeccakA.ValueHash256);
        StateId canonicalFork = new(2, TestItem.KeccakB.ValueHash256);
        Add(State(0), State(1));
        Add(State(1), State(2));
        Add(State(1), canonicalFork);
        Add(State(0), orphan);
        Add(orphan, orphanChild);
        Add(State(0), orphanChild, compacted: true);
        using PbtSnapshot? lease = _repository.FindSnapshotToPersist(orphanChild, orphan, 32);
        Assert.That(lease, Is.Not.Null);
        _repository.RemoveSiblingAndDescendents(State(1));
        _repository.RemoveStatesUntil(1);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(_repository.HasState(orphanChild), Is.False);
            Assert.That(_repository.HasState(State(2)), Is.True);
            Assert.That(_repository.HasState(canonicalFork), Is.True);
            Assert.That(_repository.CompactedCount, Is.Zero);
            Assert.That(lease!.TryLease(), Is.True);
        }
        lease!.Dispose();
    }

    private static StateId State(int block) => block < 0 ? StateId.PreGenesis : new((ulong)block, default);

    private PbtSnapshot Snapshot(StateId from, StateId to) =>
        new(from, to, default, new PbtSnapshotContent(), _pool, PbtResourcePool.Usage.MainBlockProcessing);

    private void Add(StateId from, StateId to, bool compacted = false)
    {
        PbtSnapshot snapshot = Snapshot(from, to);
        Assert.That(compacted ? _repository.TryAddCompacted(snapshot) : _repository.TryAdd(snapshot), Is.True);
    }
}
