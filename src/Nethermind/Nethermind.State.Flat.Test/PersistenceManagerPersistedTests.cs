// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.State.Flat.PersistedSnapshots;
using Nethermind.State.Flat.PersistedSnapshots.Storage;
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
        using ArenaManager smallArena = new(Path.Combine(_testDir, "arenas", "base"), 0, maxArenaSize: 4096);
        using BlobArenaManager smallBlobs = new(Path.Combine(_testDir, "blobs", "small"), 1024 * 1024, PersistedSnapshotTier.Persisted);
        using PersistedSnapshotRepository repo = new(smallArena, smallBlobs, new MemDb(), new FlatDbConfig(), new PersistedSnapshotBloomFilterManager(), LimboLogs.Instance);
        repo.LoadFromCatalog();

        IFlatDbConfig config = new FlatDbConfig();
        _ = new PersistedSnapshotCompactor(
            repo, smallArena, config, ScheduleHelper.CreateWithOffset(config, 0),
            LimboLogs.Instance, new PersistedSnapshotBloomFilterManager(),
            minCompactSize: config.MinCompactSize,
            maxCompactSize: config.CompactSize / 2);

        StateId s0 = new(0, Keccak.EmptyTreeHash);
        StateId s1 = new(1, Keccak.Compute("1"));
        SnapshotContent content = new();
        content.Accounts[TestItem.AddressA] = Build.An.Account.WithBalance(500).TestObject;
        Snapshot snap = new(s0, s1, content, _pool, ResourcePool.Usage.MainBlockProcessing);

        repo.ConvertSnapshotToPersistedSnapshot(snap).Dispose();

        Assert.That(repo.SnapshotCount, Is.EqualTo(1));
        Assert.That(repo.TryLeaseSnapshotTo(s1, out PersistedSnapshot? snapshot), Is.True);
        snapshot!.Dispose();
    }

    [Test]
    public void PrunePersistedSnapshots_RemovesOldSnapshots()
    {
        using ArenaManager smallArena = new(Path.Combine(_testDir, "arenas", "base"), 0, maxArenaSize: 4096);
        using BlobArenaManager smallBlobs = new(Path.Combine(_testDir, "blobs", "small"), 1024 * 1024, PersistedSnapshotTier.Persisted);
        using PersistedSnapshotRepository repo = new(smallArena, smallBlobs, new MemDb(), new FlatDbConfig(), new PersistedSnapshotBloomFilterManager(), LimboLogs.Instance);
        repo.LoadFromCatalog();

        IFlatDbConfig config = new FlatDbConfig();
        _ = new PersistedSnapshotCompactor(
            repo, smallArena, config, ScheduleHelper.CreateWithOffset(config, 0),
            LimboLogs.Instance, new PersistedSnapshotBloomFilterManager(),
            minCompactSize: config.MinCompactSize,
            maxCompactSize: config.CompactSize / 2);

        // Persist snapshots at various block heights
        StateId s0 = new(0, Keccak.EmptyTreeHash);
        StateId s1 = new(1, Keccak.Compute("1"));
        StateId s3 = new(3, Keccak.Compute("3"));
        StateId s6 = new(6, Keccak.Compute("6"));

        SnapshotContent c1 = new();
        c1.Accounts[TestItem.AddressA] = Build.An.Account.WithBalance(1).TestObject;
        repo.ConvertSnapshotToPersistedSnapshot(new Snapshot(s0, s1, c1, _pool, ResourcePool.Usage.MainBlockProcessing)).Dispose();

        SnapshotContent c2 = new();
        c2.Accounts[TestItem.AddressB] = Build.An.Account.WithBalance(2).TestObject;
        repo.ConvertSnapshotToPersistedSnapshot(new Snapshot(s1, s3, c2, _pool, ResourcePool.Usage.MainBlockProcessing)).Dispose();

        SnapshotContent c3 = new();
        c3.Accounts[TestItem.AddressC] = Build.An.Account.WithBalance(3).TestObject;
        repo.ConvertSnapshotToPersistedSnapshot(new Snapshot(s3, s6, c3, _pool, ResourcePool.Usage.MainBlockProcessing)).Dispose();

        Assert.That(repo.SnapshotCount, Is.EqualTo(3));

        // Remove states until block 5 (removes snapshots with To < 5, i.e., s1 and s3)
        repo.RemoveStatesUntil(5);

        Assert.That(repo.SnapshotCount, Is.EqualTo(1)); // Only s6 remains
    }

    [Test]
    public void RemoveSiblingAndDescendents_CrossTier_PrunesPersistedOrphans_KeepsCanonicalThroughPersistedAncestor()
    {
        using ArenaManager arena = new(Path.Combine(_testDir, "arenas", "base"), 0, maxArenaSize: 4096);
        using BlobArenaManager blobs = new(Path.Combine(_testDir, "blobs", "small"), 1024 * 1024, PersistedSnapshotTier.Persisted);
        using PersistedSnapshotRepository repo = new(arena, blobs, new MemDb(), new FlatDbConfig(), new PersistedSnapshotBloomFilterManager(), LimboLogs.Instance);
        repo.LoadFromCatalog();

        SnapshotRepository snapRepo = new(repo, LimboLogs.Instance);

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
        PersistToTier(repo, s0, s1);
        PersistToTier(repo, s1, s2);
        PersistToTier(repo, s2, c3);
        PersistToTier(repo, c3, c4);
        PersistToTier(repo, s2, nc3);
        PersistToTier(repo, nc3, nc4);

        // In-memory canonical C5 whose parent C4 lives only in the persisted tier — reachability
        // to C3 therefore has to cross from the in-memory tier into the persisted tier.
        AddInMemory(snapRepo, c4, c5);

        snapRepo.RemoveSiblingAndDescendents(c3);

        Assert.That(LeasePresent(repo, nc4), Is.False, "orphan NC4 above the persisted block should be pruned from the persisted tier");
        Assert.That(LeasePresent(repo, c4), Is.True, "canonical C4 should be kept");
        Assert.That(repo.HasBaseSnapshot(c3), Is.True, "canonical target C3 should be kept");
        Assert.That(repo.HasBaseSnapshot(nc3), Is.True, "NC3 at the persisted block is left to RemoveStatesUntil");
        Assert.That(snapRepo.HasState(c5), Is.True, "canonical in-memory C5 reachable through persisted C4 must be kept");
    }

    [Test]
    public void RemoveSiblingAndDescendents_PersistedLinearChain_RemovesNothing()
    {
        using ArenaManager arena = new(Path.Combine(_testDir, "arenas", "base"), 0, maxArenaSize: 4096);
        using BlobArenaManager blobs = new(Path.Combine(_testDir, "blobs", "small"), 1024 * 1024, PersistedSnapshotTier.Persisted);
        using PersistedSnapshotRepository repo = new(arena, blobs, new MemDb(), new FlatDbConfig(), new PersistedSnapshotBloomFilterManager(), LimboLogs.Instance);
        repo.LoadFromCatalog();

        SnapshotRepository snapRepo = new(repo, LimboLogs.Instance);

        StateId s0 = new(0, Keccak.EmptyTreeHash);
        StateId s1 = new(1, Keccak.Compute("1"));
        StateId s2 = new(2, Keccak.Compute("2"));
        StateId s3 = new(3, Keccak.Compute("3"));
        PersistToTier(repo, s0, s1);
        PersistToTier(repo, s1, s2);
        PersistToTier(repo, s2, s3);

        int before = repo.SnapshotCount;
        snapRepo.RemoveSiblingAndDescendents(s1);

        Assert.That(repo.SnapshotCount, Is.EqualTo(before), "a linear persisted chain has no fork; nothing should be pruned");
        Assert.That(repo.HasBaseSnapshot(s2), Is.True);
        Assert.That(repo.HasBaseSnapshot(s3), Is.True);
    }

    private void PersistToTier(PersistedSnapshotRepository repo, StateId from, StateId to)
    {
        SnapshotContent content = new();
        content.Accounts[TestItem.AddressA] = Build.An.Account.WithBalance(1).TestObject;
        repo.ConvertSnapshotToPersistedSnapshot(new Snapshot(from, to, content, _pool, ResourcePool.Usage.MainBlockProcessing)).Dispose();
    }

    private void AddInMemory(SnapshotRepository snapRepo, StateId from, StateId to)
    {
        SnapshotContent content = new();
        content.Accounts[TestItem.AddressB] = Build.An.Account.WithBalance(1).TestObject;
        snapRepo.TryAddSnapshot(new Snapshot(from, to, content, _pool, ResourcePool.Usage.MainBlockProcessing));
        snapRepo.AddStateId(to);
    }

    private static bool LeasePresent(PersistedSnapshotRepository repo, StateId to)
    {
        if (!repo.TryLeaseSnapshotTo(to, out PersistedSnapshot? snapshot)) return false;
        snapshot!.Dispose();
        return true;
    }
}
