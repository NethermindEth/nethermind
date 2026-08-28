// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.State.Flat.PersistedSnapshots;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test;

[TestFixture]
public class PersistenceManagerPersistedTests
{
    private string _testDir = null!;
    private ResourcePool _pool = null!;

    [SetUp]
    public void SetUp()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"nethermind_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDir);
        _pool = new ResourcePool(new FlatDbConfig());
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_testDir))
            Directory.Delete(_testDir, recursive: true);
    }

    [Test]
    public void ConvertToPersistedSnapshot_PersistsViaManager()
    {
        using FlatTestContainer tier = new(arenaFileSizeBytes: 4096);
        SnapshotRepository repo = tier.Repository;

        StateId s0 = new(0, Keccak.EmptyTreeHash);
        StateId s1 = new(1, Keccak.Compute("1"));
        SnapshotContent content = new();
        content.Accounts[TestItem.AddressA] = Build.An.Account.WithBalance(500).TestObject;
        Snapshot snap = new(s0, s1, content, _pool, ResourcePool.Usage.MainBlockProcessing);

        tier.ConvertToPersistedBase(snap).Dispose();

        Assert.That(repo.PersistedSnapshotCount, Is.EqualTo(1));
        Assert.That(repo.TryLeasePersistedState(s1, SnapshotTier.PersistedBase, out PersistedSnapshot? snapshot), Is.True);
        snapshot!.Dispose();
    }

    [Test]
    public void PrunePersistedSnapshots_RemovesOldSnapshots()
    {
        using FlatTestContainer tier = new(arenaFileSizeBytes: 4096);
        SnapshotRepository repo = tier.Repository;

        StateId s0 = new(0, Keccak.EmptyTreeHash);
        StateId s1 = new(1, Keccak.Compute("1"));
        StateId s3 = new(3, Keccak.Compute("3"));
        StateId s6 = new(6, Keccak.Compute("6"));

        SnapshotContent c1 = new();
        c1.Accounts[TestItem.AddressA] = Build.An.Account.WithBalance(1).TestObject;
        tier.ConvertToPersistedBase(new Snapshot(s0, s1, c1, _pool, ResourcePool.Usage.MainBlockProcessing)).Dispose();

        SnapshotContent c2 = new();
        c2.Accounts[TestItem.AddressB] = Build.An.Account.WithBalance(2).TestObject;
        tier.ConvertToPersistedBase(new Snapshot(s1, s3, c2, _pool, ResourcePool.Usage.MainBlockProcessing)).Dispose();

        SnapshotContent c3 = new();
        c3.Accounts[TestItem.AddressC] = Build.An.Account.WithBalance(3).TestObject;
        tier.ConvertToPersistedBase(new Snapshot(s3, s6, c3, _pool, ResourcePool.Usage.MainBlockProcessing)).Dispose();

        Assert.That(repo.PersistedSnapshotCount, Is.EqualTo(3));

        // Snapshots with To.BlockNumber < 5 are removed (s1, s3); s6 survives.
        repo.RemovePersistedStatesUntil(5);

        Assert.That(repo.PersistedSnapshotCount, Is.EqualTo(1));
    }

    [Test]
    public void RemoveSiblingAndDescendents_CrossTier_PrunesPersistedOrphans_KeepsCanonicalThroughPersistedAncestor()
    {
        using FlatTestContainer tier = new(arenaFileSizeBytes: 4096);
        SnapshotRepository repo = tier.Repository;

        StateId s0 = new(0, Keccak.EmptyTreeHash);
        StateId s1 = new(1, Keccak.Compute("1"));
        StateId s2 = new(2, Keccak.Compute("2"));
        StateId c3 = new(3, Keccak.Compute("c3"));
        StateId c4 = new(4, Keccak.Compute("c4"));
        StateId nc3 = new(3, Keccak.Compute("nc3"));
        StateId nc4 = new(4, Keccak.Compute("nc4"));
        StateId c5 = new(5, Keccak.Compute("c5"));

        // Persisted tier: common chain s0->s1->s2, canonical s2->C3->C4, and a non-canonical
        // fork s2->NC3->NC4 diverging at block 3.
        PersistToTier(tier, s0, s1);
        PersistToTier(tier, s1, s2);
        PersistToTier(tier, s2, c3);
        PersistToTier(tier, c3, c4);
        PersistToTier(tier, s2, nc3);
        PersistToTier(tier, nc3, nc4);

        // In-memory canonical C5 whose parent C4 lives only in the persisted tier — reachability
        // to C3 therefore has to cross from the in-memory tier into the persisted tier.
        AddInMemory(repo, c4, c5);

        repo.RemoveSiblingAndDescendents(c3);

        Assert.That(LeasePresent(repo, nc4), Is.False, "orphan NC4 above the persisted block should be pruned from the persisted tier");
        Assert.That(LeasePresent(repo, c4), Is.True, "canonical C4 should be kept");
        Assert.That(repo.HasBasePersistedSnapshot(c3), Is.True, "canonical target C3 should be kept");
        Assert.That(repo.HasBasePersistedSnapshot(nc3), Is.True, "NC3 at the persisted block is left to RemoveStatesUntil");
        Assert.That(repo.HasState(c5), Is.True, "canonical in-memory C5 reachable through persisted C4 must be kept");
    }

    [Test]
    public void RemoveSiblingAndDescendents_PersistedOrphanAboveInMemoryTip_IsPruned()
    {
        using FlatTestContainer tier = new(arenaFileSizeBytes: 4096);
        SnapshotRepository repo = tier.Repository;

        StateId s0 = new(0, Keccak.EmptyTreeHash);
        StateId s1 = new(1, Keccak.Compute("1"));
        StateId s2 = new(2, Keccak.Compute("2"));
        StateId c3 = new(3, Keccak.Compute("c3"));
        StateId nc3 = new(3, Keccak.Compute("nc3"));
        StateId nc4 = new(4, Keccak.Compute("nc4"));

        // Persisted tier: common chain s0->s1->s2, canonical s2->C3, and a non-canonical fork
        // s2->NC3->NC4 diverging at block 3 — NC4 is an orphan at block 4.
        PersistToTier(tier, s0, s1);
        PersistToTier(tier, s1, s2);
        PersistToTier(tier, s2, c3);
        PersistToTier(tier, s2, nc3);
        PersistToTier(tier, nc3, nc4);

        // In-memory tip sits at the canonical block (3), BELOW the persisted orphan NC4 (block 4).
        // The orphan walk's upper bound must come from the persisted tier, not the in-memory tip,
        // or NC4 is never visited.
        AddInMemory(repo, s2, c3);

        repo.RemoveSiblingAndDescendents(c3);

        Assert.That(LeasePresent(repo, nc4), Is.False, "persisted orphan NC4 above the in-memory tip should be pruned");
        Assert.That(repo.HasBasePersistedSnapshot(c3), Is.True, "canonical C3 should be kept");
        Assert.That(repo.HasBasePersistedSnapshot(nc3), Is.True, "NC3 at the persisted block is left to RemoveStatesUntil");
    }

    [Test]
    public void RemoveSiblingAndDescendents_PersistedLinearChain_RemovesNothing()
    {
        using FlatTestContainer tier = new(arenaFileSizeBytes: 4096);
        SnapshotRepository repo = tier.Repository;

        StateId s0 = new(0, Keccak.EmptyTreeHash);
        StateId s1 = new(1, Keccak.Compute("1"));
        StateId s2 = new(2, Keccak.Compute("2"));
        StateId s3 = new(3, Keccak.Compute("3"));
        PersistToTier(tier, s0, s1);
        PersistToTier(tier, s1, s2);
        PersistToTier(tier, s2, s3);

        int before = repo.PersistedSnapshotCount;
        repo.RemoveSiblingAndDescendents(s1);

        Assert.That(repo.PersistedSnapshotCount, Is.EqualTo(before), "a linear persisted chain has no fork; nothing should be pruned");
        Assert.That(repo.HasBasePersistedSnapshot(s2), Is.True);
        Assert.That(repo.HasBasePersistedSnapshot(s3), Is.True);
    }

    private void PersistToTier(FlatTestContainer tier, StateId from, StateId to)
    {
        SnapshotContent content = new();
        content.Accounts[TestItem.AddressA] = Build.An.Account.WithBalance(1).TestObject;
        tier.ConvertToPersistedBase(new Snapshot(from, to, content, _pool, ResourcePool.Usage.MainBlockProcessing)).Dispose();
    }

    private void AddInMemory(SnapshotRepository repo, StateId from, StateId to)
    {
        SnapshotContent content = new();
        content.Accounts[TestItem.AddressB] = Build.An.Account.WithBalance(1).TestObject;
        repo.TryAdd(new Snapshot(from, to, content, _pool, ResourcePool.Usage.MainBlockProcessing), SnapshotTier.InMemoryBase);
        repo.AddStateId(to);
    }

    private static bool LeasePresent(SnapshotRepository repo, StateId to)
    {
        if (!repo.TryLeasePersistedState(to, SnapshotTier.PersistedBase, out PersistedSnapshot? snapshot)) return false;
        snapshot!.Dispose();
        return true;
    }
}
