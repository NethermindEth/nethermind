// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Nethermind.Core.Buffers;
using Nethermind.State.Flat.PersistedSnapshots.Storage;
using Nethermind.State.Pbt.PersistedSnapshots;
using Nethermind.State.Flat.Persistence.BloomFilter;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Monitoring.Config;
using NUnit.Framework;
using Nethermind.State.Flat;
using Nethermind.State.Pbt.Snapshot;

namespace Nethermind.State.Pbt.Test;

public class PbtSnapshotRepositoryTests
{
    private readonly PbtResourcePool _pool = new(new PbtConfig());
    private PbtSnapshotRepository _repository = null!;

    [SetUp]
    public void SetUp() => _repository = new(new MetricsConfig());

    [TearDown]
    public void TearDown() => _repository.Dispose();

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

    [TestCase(0, 64, 32, 32, 32)]
    [TestCase(32, 64, 32, 32, 64)]
    [TestCase(0, 64, 0, 32, 1)]
    [TestCase(0, 64, 8, 32, 8)]
    [TestCase(3, 64, 32, 32, 4)]
    [TestCase(32, 35, 32, 32, 33)]
    [TestCase(-1, 64, 32, 32, 0)]
    [TestCase(0, 2, 2, 1, 1)]
    public void FindSnapshotToPersist_PrefersFullUnitsWithSmallerAndBaseFallback(int floor, int head, int compactedWidth, int compactSize, int expected)
    {
        for (int block = 0; block <= head; block++) Add(State(block - 1), State(block));
        if (compactedWidth > 0)
            for (int block = compactedWidth; block <= head; block += compactedWidth)
                Add(State(block - compactedWidth), State(block), compacted: true);

        using PbtSnapshot? candidate = _repository.FindSnapshotToPersist(State(head), State(floor), (ulong)compactSize);
        Assert.That(candidate, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(candidate!.From, Is.EqualTo(State(floor)));
            Assert.That(candidate.To, Is.EqualTo(State(expected)));
            Assert.That(candidate.To.BlockNumber - candidate.From.BlockNumber, Is.LessThanOrEqualTo((ulong)compactSize));
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
    public void Compaction_RetainsFullBoundariesAndRetiresOlderPartialUnits([Values(0, 3)] int offset)
    {
        PbtConfig config = new() { CompactSize = 8, CompactionOffset = offset };
        using MemDb metadata = new();
        PbtSnapshotCompactor compactor = new(_pool, PbtCoreRegistration.CreateCompactionSchedule(metadata, config, LimboLogs.Instance), _repository, config, LimboLogs.Instance);
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

    [Test]
    public void Retained_availability_requires_base_and_admission_borrows_ownership(
        [Values(SnapshotTier.PersistedBase, SnapshotTier.PersistedSmallCompacted, SnapshotTier.PersistedCompactSized, SnapshotTier.PersistedLargeCompacted)] SnapshotTier tier)
    {
        using PbtRetainedTestStore store = new();
        using PbtRetainedSnapshot retained = Retained(store, State(0), State(1), tier);
        Assert.That(_repository.TryAddRetained(retained), Is.True);
        Assert.That(_repository.TryAddRetained(retained), Is.False);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(_repository.HasState(State(1)), Is.EqualTo(tier == SnapshotTier.PersistedBase));
            Assert.That(_repository.ContainsRetainedSource(retained), Is.True);
            Assert.That(_repository.RetainedCount, Is.EqualTo(1));
        }
        _repository.Dispose();
        Assert.That(retained.TryLease(), Is.True);
        retained.Dispose();
    }

    [Test]
    public void Read_priority_matches_flat([Values(SnapshotTier.PersistedLargeCompacted, SnapshotTier.PersistedCompactSized,
        SnapshotTier.InMemoryCompacted, SnapshotTier.InMemoryBase, SnapshotTier.PersistedSmallCompacted, SnapshotTier.PersistedBase)] SnapshotTier expected)
    {
        using PbtRetainedTestStore store = new();
        SnapshotTier[] priority = [SnapshotTier.PersistedLargeCompacted, SnapshotTier.PersistedCompactSized,
            SnapshotTier.InMemoryCompacted, SnapshotTier.InMemoryBase, SnapshotTier.PersistedSmallCompacted, SnapshotTier.PersistedBase];
        int first = Array.IndexOf(priority, expected);
        for (int i = first; i < priority.Length; i++)
        {
            SnapshotTier tier = priority[i];
            // Different depths avoid a catalog-key collision while all retained edges overshoot the floor.
            StateId from = State(8 - i);
            if (tier.IsPersisted())
            {
                using PbtRetainedSnapshot retained = Retained(store, from, State(10), tier);
                Assert.That(_repository.TryAddRetained(retained), Is.True);
            }
            else Add(State(9), State(10), tier == SnapshotTier.InMemoryCompacted);
        }
        using PbtSnapshotChain? chain = _repository.TryLeaseReadChain(State(10), State(9));
        Assert.That(chain, Is.Not.Null);
        Assert.That(chain!.Layers[0].Tier, Is.EqualTo(expected));
        _repository.Dispose();
    }

    [Test]
    public void Retained_floor_overshoot_is_allowed_but_memory_overshoot_is_not([Values] bool retained)
    {
        using PbtRetainedTestStore store = new();
        if (retained)
        {
            using PbtRetainedSnapshot edge = Retained(store, State(0), State(4), SnapshotTier.PersistedCompactSized);
            _repository.TryAddRetained(edge);
        }
        else Add(State(0), State(4), compacted: true);
        using PbtSnapshotChain? chain = _repository.TryLeaseReadChain(State(4), State(2));
        Assert.That(chain is not null, Is.EqualTo(retained));
        _repository.Dispose();
    }

    [Test]
    public void Retained_transition_never_returns_to_memory()
    {
        using PbtRetainedTestStore store = new();
        Add(State(0), State(1));
        using PbtRetainedSnapshot edge = Retained(store, State(1), State(2), SnapshotTier.PersistedBase);
        _repository.TryAddRetained(edge);
        using PbtSnapshotChain? chain = _repository.TryLeaseReadChain(State(2), State(0));
        Assert.That(chain, Is.Null);
        _repository.Dispose();
    }

    [Test]
    public void Persistence_rejects_wide_and_nonfinalized_edges_without_shadowing_narrow_candidate()
    {
        using PbtRetainedTestStore store = new();
        Add(State(0), State(1));
        Add(State(1), State(2));
        using PbtRetainedSnapshot edge = Retained(store, State(0), State(2), SnapshotTier.PersistedLargeCompacted);
        _repository.TryAddRetained(edge);
        using PbtSnapshotLease? candidate = _repository.FindCandidateToPersist(State(2), State(0), 1, state => state == State(1));
        Assert.That(candidate!.To, Is.EqualTo(State(1)));
        using PbtSnapshotLease? rejected = _repository.FindCandidateToPersist(State(2), State(0), 32, static _ => false);
        Assert.That(rejected, Is.Null);
        _repository.Dispose();
    }

    [Test]
    public void Walk_terminates_cycles_and_rejects_same_height_wrong_root()
    {
        Add(State(2), State(1));
        Add(State(1), State(2));
        using PbtSnapshotChain? cycle = _repository.TryLeaseReadChain(State(2), State(0));
        Assert.That(cycle, Is.Null);
        StateId sibling = new(1, TestItem.KeccakA.ValueHash256);
        using PbtSnapshotChain? wrongRoot = _repository.TryLeaseReadChain(State(2), sibling);
        Assert.That(wrongRoot, Is.Null);
    }

    [Test]
    public void Retained_compaction_is_best_effort_and_sentinel_aware()
    {
        using PbtRetainedTestStore store = new();
        using PbtRetainedSnapshot first = Retained(store, StateId.PreGenesis, State(0), SnapshotTier.PersistedBase);
        using PbtRetainedSnapshot second = Retained(store, State(0), State(1), SnapshotTier.PersistedBase);
        _repository.TryAddRetained(first); _repository.TryAddRetained(second);
        using PbtSnapshotChain? full = _repository.TryLeaseRetainedChain(State(1), ulong.MaxValue);
        using PbtSnapshotChain? partial = _repository.TryLeaseRetainedChain(State(1), 0);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(full!.Layers.Count, Is.EqualTo(2));
            Assert.That(full.Layers[0].From, Is.EqualTo(StateId.PreGenesis));
            Assert.That(partial!.Layers.Count, Is.EqualTo(1));
            Assert.That(_repository.GetLastSnapshotId(), Is.EqualTo(State(1)));
            Assert.That(_repository.HasState(State(0)), Is.True);
            Assert.That(_repository.HasState(State(1)), Is.True);
            Assert.That(_repository.RetainedCount, Is.EqualTo(2));
        }
        _repository.Dispose();
    }

    [Test]
    public void Finality_callback_runs_outside_index_lock()
    {
        Add(State(0), State(1));
        using PbtSnapshotLease? lease = _repository.FindCandidateToPersist(State(1), State(0), 32, _ =>
        {
            Task<int> query = Task.Run(() => _repository.Count);
            Assert.That(query.Wait(TimeSpan.FromSeconds(5)), Is.True);
            return true;
        });
        Assert.That(lease, Is.Not.Null);
    }

    [Test]
    public void Skipped_wrong_root_edge_does_not_shadow_usable_read_edge()
    {
        StateId sibling = new(0, TestItem.KeccakA.ValueHash256);
        Add(sibling, State(1), compacted: true);
        Add(State(0), State(1));
        using PbtSnapshotChain? chain = _repository.TryLeaseReadChain(State(1), State(0));
        Assert.That(chain!.Layers[0].Tier, Is.EqualTo(SnapshotTier.InMemoryBase));
    }

    [Test]
    public void Retained_deletion_failure_keeps_index_and_reader_ownership()
    {
        using PbtRetainedTestStore store = new();
        FailingCatalog catalog = new();
        using PbtSnapshotRepository repository = new(new MetricsConfig(), catalog, new PbtRetainedPublicationGate());
        using PbtRetainedSnapshot retained = Retained(store, State(0), State(1), SnapshotTier.PersistedBase);
        repository.TryAddRetained(retained);
        using PbtSnapshotChain? held = repository.TryLeaseReadChain(State(1), State(0));
        Assert.Throws<IOException>(() => repository.RemoveRetainedStatesBefore(2));
        Assert.That(repository.HasState(State(1)), Is.True);
        catalog.Fail = false;
        repository.RemoveRetainedStatesBefore(2);
        Assert.That(repository.HasState(State(1)), Is.False);
        Assert.That(held!.Layers[0].Retained!.TryLease(), Is.True);
        held.Layers[0].Retained!.Dispose();
    }

    [Test]
    public void Retained_sibling_descendants_are_pruned_but_canonical_forks_survive()
    {
        using PbtRetainedTestStore store = new();
        StateId orphan = new(1, TestItem.KeccakA.ValueHash256);
        StateId orphanChild = new(2, TestItem.KeccakA.ValueHash256);
        StateId fork = new(2, TestItem.KeccakB.ValueHash256);
        foreach ((StateId from, StateId to) in new[] { (State(0), State(1)), (State(0), orphan), (State(1), State(2)), (State(1), fork), (orphan, orphanChild) })
        {
            using PbtRetainedSnapshot snapshot = Retained(store, from, to, SnapshotTier.PersistedBase);
            _repository.TryAddRetained(snapshot);
        }
        _repository.RemoveSiblingAndDescendents(State(1));
        _repository.RemoveRetainedStatesBefore(2);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(_repository.HasState(orphanChild), Is.False);
            Assert.That(_repository.HasState(State(2)), Is.True);
            Assert.That(_repository.HasState(fork), Is.True);
            Assert.That(_repository.RetainedCount, Is.EqualTo(2));
        }
        _repository.Dispose();
    }

    [Test]
    public void Descendant_pruning_enforces_the_retained_transition([Values] bool retainedFirst)
    {
        using PbtRetainedTestStore store = new();
        StateId sibling = new(0, TestItem.KeccakA.ValueHash256);
        Add(StateId.PreGenesis, State(0));
        Add(StateId.PreGenesis, sibling);
        for (int block = 1; block <= 2; block++)
        {
            if ((block == 1) == retainedFirst)
            {
                using PbtRetainedSnapshot snapshot = Retained(store, State(block - 1), State(block), SnapshotTier.PersistedBase);
                _repository.TryAddRetained(snapshot);
            }
            else Add(State(block - 1), State(block));
        }
        _repository.RemoveSiblingAndDescendents(State(0));
        using PbtSnapshotChain? chain = _repository.TryLeaseReadChain(State(2), State(0));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(_repository.HasState(State(2)), Is.EqualTo(retainedFirst));
            Assert.That(chain is not null, Is.EqualTo(retainedFirst));
        }
        _repository.Dispose();
    }

    [Test]
    public void Rewind_prunes_every_tier_and_preserves_held_chains([Values] SnapshotTier tier)
    {
        using PbtRetainedTestStore store = new();
        StateId abandoned = new(2, TestItem.KeccakB.ValueHash256);
        Add(State(0), State(1));
        Add(State(1), State(2));
        Add(State(1), abandoned);
        if (tier.IsPersisted())
        {
            using PbtRetainedSnapshot parent = Retained(store, State(0), State(1), SnapshotTier.PersistedBase);
            _repository.TryAddRetained(parent);
            using PbtRetainedSnapshot canonical = Retained(store, State(1), State(2), tier);
            using PbtRetainedSnapshot orphan = Retained(store, State(1), abandoned, tier);
            _repository.TryAddRetained(canonical);
            _repository.TryAddRetained(orphan);
        }
        else if (tier == SnapshotTier.InMemoryCompacted)
        {
            Add(State(0), State(2), compacted: true);
            Add(State(0), abandoned, compacted: true);
        }
        using PbtSnapshotChain? held = _repository.TryLeaseReadChain(abandoned, State(0));
        Assert.That(held, Is.Not.Null);
        Assert.That(_repository.TryRemoveUnreachableFrom(State(1), State(0), out int removed), Is.True);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(removed, Is.EqualTo(2));
            Assert.That(_repository.HasState(State(1)), Is.True);
            Assert.That(_repository.HasState(State(2)), Is.False);
            Assert.That(_repository.HasState(abandoned), Is.False);
            Assert.That(_repository.CompactedCount, Is.Zero);
            Assert.That(_repository.RetainedCount, Is.EqualTo(tier.IsPersisted() ? 1 : 0));
            Assert.That(_repository.GetLastCommittedStateId(), Is.EqualTo(State(1)));
            Assert.That(held!.Layers[^1].To, Is.EqualTo(abandoned));
        }
        foreach (PbtSnapshotLease lease in held!.Layers)
        {
            if (lease.Retained is { } retained)
            {
                Assert.That(retained.TryGetAccount(TestItem.KeccakA.ValueHash256, out _), Is.False);
            }
            else Assert.That(lease.Memory!.Content.Accounts, Is.Empty);
        }
        held.Dispose();
        _repository.Dispose();
    }

    [Test]
    public void Rewind_rejects_unknown_or_divergent_base_without_mutation([Values] bool unavailable)
    {
        Add(State(0), State(1));
        Add(State(1), State(2));
        StateId target = unavailable ? State(9) : State(1);
        StateId persisted = unavailable ? State(0) : new(0, TestItem.KeccakB.ValueHash256);
        Assert.That(_repository.TryRemoveUnreachableFrom(target, persisted, out int removed), Is.False);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(removed, Is.Zero);
            Assert.That(_repository.Count, Is.EqualTo(2));
            Assert.That(_repository.GetLastCommittedStateId(), Is.EqualTo(State(2)));
        }
    }

    [Test]
    public void Rewind_to_exact_base_needs_no_snapshot_and_never_rewinds_it()
    {
        Add(State(0), State(1));
        Add(State(1), State(2));
        Assert.That(_repository.TryRemoveUnreachableFrom(State(0), State(0), out int removed), Is.True);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(removed, Is.EqualTo(2));
            Assert.That(_repository.Count, Is.Zero);
            Assert.That(_repository.GetLastCommittedStateId(), Is.EqualTo(State(0)));
        }
    }

    [Test]
    public async Task Rewind_preserves_memory_admitted_after_candidate_capture()
    {
        using PbtRetainedTestStore store = new();
        using System.Threading.ManualResetEventSlim deleting = new();
        using System.Threading.ManualResetEventSlim resume = new();
        CallbackCatalog catalog = new(() =>
        {
            deleting.Set();
            Assert.That(resume.Wait(TimeSpan.FromSeconds(10)), Is.True);
        });
        using PbtSnapshotRepository repository = new(new MetricsConfig(), catalog, new PbtRetainedPublicationGate());
        repository.TryAdd(Snapshot(State(0), State(1)));
        using (PbtRetainedSnapshot orphan = Retained(store, State(1), State(2), SnapshotTier.PersistedBase)) repository.TryAddRetained(orphan);
        Task<bool> rewind = Task.Run(() => repository.TryRemoveUnreachableFrom(State(1), State(0), out _));
        try
        {
            Assert.That(deleting.Wait(TimeSpan.FromSeconds(10)), Is.True);
            repository.TryAdd(Snapshot(State(1), State(3)));
        }
        finally { resume.Set(); }
        Assert.That(await rewind.WaitAsync(TimeSpan.FromSeconds(10)), Is.True);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(repository.HasState(State(2)), Is.False);
            Assert.That(repository.HasState(State(3)), Is.True);
            Assert.That(repository.GetLastCommittedStateId(), Is.EqualTo(State(1)));
        }
    }

    [Test]
    public void Rewind_failed_catalog_delete_does_not_publish_reset_or_release_retained_state()
    {
        using PbtRetainedTestStore store = new();
        FailingCatalog catalog = new();
        using PbtSnapshotRepository repository = new(new MetricsConfig(), catalog, new PbtRetainedPublicationGate());
        repository.TryAdd(Snapshot(State(0), State(1)));
        repository.TryAdd(Snapshot(State(1), State(3)));
        using (PbtRetainedSnapshot orphan = Retained(store, State(1), State(2), SnapshotTier.PersistedBase)) repository.TryAddRetained(orphan);
        Assert.Throws<IOException>(() => repository.TryRemoveUnreachableFrom(State(1), State(0), out _));
        Assert.That(repository.GetLastCommittedStateId(), Is.EqualTo(State(3)));
        Assert.That(repository.HasState(State(2)), Is.True);
        catalog.Fail = false;
        Assert.That(repository.TryRemoveUnreachableFrom(State(1), State(0), out _), Is.True);
        Assert.That(repository.GetLastCommittedStateId(), Is.EqualTo(State(1)));
    }

    [Test]
    public void Shared_retained_bloom_preserves_history_and_excludes_forks_and_crossing_edges()
    {
        using PbtRetainedTestStore store = new();
        using PbtSnapshotRepository repository = new(new MetricsConfig());
        PbtRetainedSnapshot first = BloomSnapshot(store, State(0), State(1), SnapshotTier.PersistedBase, 1);
        PbtRetainedSnapshot second = BloomSnapshot(store, State(1), State(2), SnapshotTier.PersistedBase, 2);
        PbtRetainedSnapshot third = BloomSnapshot(store, State(2), State(3), SnapshotTier.PersistedBase, 3);
        using PbtRetainedSnapshot fork = BloomSnapshot(store, State(1), new StateId(2, TestItem.KeccakB.ValueHash256), SnapshotTier.PersistedBase, 4);
        using PbtRetainedSnapshot crossing = BloomSnapshot(store, StateId.PreGenesis, State(2), SnapshotTier.PersistedSmallCompacted, 5);
        using PbtRetainedSnapshot wrongParent = BloomSnapshot(store, new StateId(1, TestItem.KeccakB.ValueHash256), State(3), SnapshotTier.PersistedSmallCompacted, 6);
        repository.TryAddRetained(first);
        repository.TryAddRetained(second);
        repository.TryAddRetained(third);
        repository.TryAddRetained(fork);
        repository.TryAddRetained(crossing);
        repository.TryAddRetained(wrongParent);
        RefCountedBloomFilter shared = new(new BloomFilter(32, 14));
        try
        {
            repository.ShareBloomAcrossRange(State(0), State(3), shared);
            for (int block = 1; block <= 3; block++)
            {
                Assert.That(repository.TryLeaseRetained(State(block), 1, SnapshotTier.PersistedBase, out PbtRetainedSnapshot? snapshot), Is.True);
                using (snapshot)
                {
                    Assert.That(snapshot!.BloomRef, Is.SameAs(shared));
                    byte[] hash = new byte[32]; hash[^1] = (byte)block;
                    Assert.That(shared.Filter.MightContain(PbtRetainedKey.BloomHash(PbtRetainedKey.CodeEntity(new ValueHash256(hash)))), Is.True);
                    Assert.That(snapshot.TryGetCode(new ValueHash256(hash), out _), Is.True);
                }
            }
            Assert.That(repository.ContainsRetainedSource(fork), Is.True);
            Assert.That(repository.ContainsRetainedSource(crossing), Is.True);
            Assert.That(repository.ContainsRetainedSource(wrongParent), Is.True);
            Assert.That(repository.ContainsRetainedSource(first), Is.False);
            Assert.That(repository.ContainsRetainedStorageSource(first), Is.True);
            repository.RemoveRetainedStatesBefore(2);
            Assert.That(repository.ContainsRetainedStorageSource(first), Is.False);
            using PbtRetainedSnapshot reconverted = BloomSnapshot(store, State(0), State(1), SnapshotTier.PersistedBase, 1);
            repository.TryAddRetained(reconverted);
            Assert.That(repository.ContainsRetainedStorageSource(first), Is.False, "a different physical conversion is not the leased source");
            byte[] firstHash = new byte[32]; firstHash[^1] = 1;
            Assert.That(first.TryGetCode(new ValueHash256(firstHash), out _), Is.True, "held old handle remains readable after rebinding");
            repository.Dispose();
        }
        finally
        {
            first.Dispose(); second.Dispose(); third.Dispose(); shared.Dispose();
        }
        Assert.Throws<InvalidOperationException>(() => shared.AcquireLease(), "shared bloom is reclaimed after all indexed twins release");
    }

    [Test]
    public void Shared_retained_bloom_does_not_inflate_capacity_for_repeated_multi_tier_keys([Values(4, 32)] int blocks)
    {
        using PbtRetainedTestStore store = new();
        using PbtSnapshotRepository repository = new(new MetricsConfig());
        for (int block = 1; block <= blocks; block++)
        {
            using PbtRetainedSnapshot snapshot = BloomSnapshot(store, State(block - 1), State(block), SnapshotTier.PersistedBase, 1);
            repository.TryAddRetained(snapshot);
            if (block > 1)
            {
                using PbtRetainedSnapshot compacted = BloomSnapshot(store, State(0), State(block), SnapshotTier.PersistedSmallCompacted, 1);
                repository.TryAddRetained(compacted);
            }
        }
        using RefCountedBloomFilter shared = new(new BloomFilter(1, 14));
        byte[] hash = new byte[32]; hash[^1] = 1;
        shared.Filter.Add(PbtRetainedKey.BloomHash(PbtRetainedKey.CodeEntity(new ValueHash256(hash))));
        repository.ShareBloomAcrossRange(State(0), State(blocks), shared);
        repository.ShareBloomAcrossRange(State(0), State(blocks), shared);
        Assert.That(shared.Filter.Count, Is.EqualTo(1));
        using BloomFilter nextCompactionBloom = new(shared.Filter.Count, 14);
        Assert.That(nextCompactionBloom.DataBytes, Is.EqualTo(64));
        repository.Dispose();
    }

    private PbtRetainedSnapshot BloomSnapshot(PbtRetainedTestStore store, StateId from, StateId to, SnapshotTier tier, byte key)
    {
        PbtSnapshotContent content = new();
        byte[] hash = new byte[32]; hash[^1] = key;
        content.Codes[new ValueHash256(hash)] = new(new byte[65537]);
        using PbtSnapshot memory = new(from, to, default, content, _pool, PbtResourcePool.Usage.MainBlockProcessing);
        using PbtRetainedSnapshot built = store.Build(memory);
        return new(new(from, to, built.Location, tier), built.Reservation, store.Blobs, store.Memory, built.BloomRef);
    }

    private sealed class CallbackCatalog(Action deleting) : ISnapshotCatalog
    {
        public void Add(CatalogEntry entry) { }
        public bool Remove(in StateId to, long depth) { deleting(); return true; }
        public IEnumerable<CatalogEntry> Load() => [];
    }

    private sealed class FailingCatalog : ISnapshotCatalog
    {
        internal bool Fail { get; set; } = true;
        public void Add(CatalogEntry entry) { }
        public bool Remove(in StateId to, long depth) => Fail ? throw new IOException("injected catalog sync failure") : true;
        public IEnumerable<CatalogEntry> Load() => [];
    }

    private PbtRetainedSnapshot Retained(PbtRetainedTestStore store, StateId from, StateId to, SnapshotTier tier)
    {
        using PbtSnapshot memory = Snapshot(from, to);
        using PbtRetainedSnapshot built = store.Build(memory);
        return new(new(from, to, built.Location, tier), built.Reservation, store.Blobs, store.Memory, built.BloomRef);
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
