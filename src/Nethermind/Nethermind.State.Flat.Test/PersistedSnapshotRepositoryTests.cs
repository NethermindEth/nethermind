// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Flat.PersistedSnapshots;
using Nethermind.State.Flat.Storage;
using Nethermind.Trie;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test;

[TestFixture]
public class PersistedSnapshotRepositoryTests
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

    private Snapshot CreateTestSnapshot(StateId from, StateId to, Address? account = null, UInt256 balance = default)
    {
        SnapshotContent content = new();
        if (account is not null)
            content.Accounts[account] = Build.An.Account.WithBalance(balance == 0 ? 1000 : balance).TestObject;
        return new Snapshot(from, to, content, _pool, ResourcePool.Usage.MainBlockProcessing);
    }

    [Test]
    public void PersistSnapshot_And_Query()
    {
        using ArenaManager smallArena = new(Path.Combine(_testDir, "arenas", "base"), 0, maxArenaSize: 4096);
        using ArenaManager largeArena = new(Path.Combine(_testDir, "arenas", "compacted"), 0, maxArenaSize: 4096);
        using BlobArenaCatalog blobCatalog = new(new MemDb());
        using BlobArenaManager smallBlobs = new(Path.Combine(_testDir, "blobs", "small"), 1024 * 1024, blobCatalog, BlobArenaPool.Small);
        using BlobArenaManager largeBlobs = new(Path.Combine(_testDir, "blobs", "large"), 1024 * 1024, blobCatalog, BlobArenaPool.Large);
        using PersistedSnapshotRepository repo = new(smallArena, smallBlobs, largeArena, largeBlobs, blobCatalog, new MemDb(), new FlatDbConfig());
        repo.LoadFromCatalog();

        StateId s0 = new(0, Keccak.EmptyTreeHash);
        StateId s1 = new(1, Keccak.Compute("1"));
        Snapshot snap = CreateTestSnapshot(s0, s1, TestItem.AddressA);

        repo.ConvertSnapshotToPersistedSnapshot(snap);
        Assert.That(repo.SnapshotCount, Is.EqualTo(1));

        // Query through the snapshot
        Assert.That(repo.TryLeaseSnapshotTo(s1, out PersistedSnapshot? persisted), Is.True);
        Assert.That(persisted!.From, Is.EqualTo(s0));
        Assert.That(persisted.To, Is.EqualTo(s1));
        Assert.That(persisted.TryGetAccount(ValueKeccak.Compute(TestItem.AddressA.Bytes), out Account? decoded), Is.True);
        Assert.That(decoded!.Balance, Is.EqualTo((UInt256)1000));
        persisted.Dispose();
    }

    [Test]
    public void NewerSnapshot_OverridesOlderValue()
    {
        using ArenaManager smallArena = new(Path.Combine(_testDir, "arenas", "base"), 0, maxArenaSize: 4096);
        using ArenaManager largeArena = new(Path.Combine(_testDir, "arenas", "compacted"), 0, maxArenaSize: 4096);
        using BlobArenaCatalog blobCatalog = new(new MemDb());
        using BlobArenaManager smallBlobs = new(Path.Combine(_testDir, "blobs", "small"), 1024 * 1024, blobCatalog, BlobArenaPool.Small);
        using BlobArenaManager largeBlobs = new(Path.Combine(_testDir, "blobs", "large"), 1024 * 1024, blobCatalog, BlobArenaPool.Large);
        using PersistedSnapshotRepository repo = new(smallArena, smallBlobs, largeArena, largeBlobs, blobCatalog, new MemDb(), new FlatDbConfig());
        repo.LoadFromCatalog();

        StateId s0 = new(0, Keccak.EmptyTreeHash);
        StateId s1 = new(1, Keccak.Compute("1"));
        StateId s2 = new(2, Keccak.Compute("2"));

        // Persist two snapshots with different state trie nodes at same path
        TreePath path = new(Keccak.Compute("path"), 4);
        byte[] rlp1 = [0xC0];
        byte[] rlp2 = [0xC1, 0x80];

        SnapshotContent content1 = new();
        content1.StateNodes[path] = new TrieNode(NodeType.Leaf, rlp1);
        Snapshot snap1 = new(s0, s1, content1, _pool, ResourcePool.Usage.MainBlockProcessing);

        SnapshotContent content2 = new();
        content2.StateNodes[path] = new TrieNode(NodeType.Leaf, rlp2);
        Snapshot snap2 = new(s1, s2, content2, _pool, ResourcePool.Usage.MainBlockProcessing);

        repo.ConvertSnapshotToPersistedSnapshot(snap1);
        repo.ConvertSnapshotToPersistedSnapshot(snap2);

        // The newest snapshot (s1→s2) should have rlp2 at the path
        Assert.That(repo.TryLeaseSnapshotTo(s2, out PersistedSnapshot? newest), Is.True);
        Assert.That(newest!.TryLoadStateNodeRlp(path, out byte[]? result), Is.True);
        Assert.That(result, Is.EqualTo(rlp2));
        newest.Dispose();
    }

    [Test]
    public void LoadFromCatalog_RestoresSnapshots()
    {
        StateId s0 = new(0, Keccak.EmptyTreeHash);
        StateId s1 = new(1, Keccak.Compute("1"));
        MemDb catalogDb = new();
        MemDb blobCatalogDb = new();

        // Session 1: persist a snapshot
        using (ArenaManager smallArena1 = new(Path.Combine(_testDir, "arenas", "base"), 0, maxArenaSize: 4096))
        using (ArenaManager largeArena1 = new(Path.Combine(_testDir, "arenas", "compacted"), 0, maxArenaSize: 4096))
        using (BlobArenaCatalog blobCatalog1 = new(blobCatalogDb))
        using (BlobArenaManager smallBlobs1 = new(Path.Combine(_testDir, "blobs", "small"), 1024 * 1024, blobCatalog1, BlobArenaPool.Small))
        using (BlobArenaManager largeBlobs1 = new(Path.Combine(_testDir, "blobs", "large"), 1024 * 1024, blobCatalog1, BlobArenaPool.Large))
        using (PersistedSnapshotRepository repo = new(smallArena1, smallBlobs1, largeArena1, largeBlobs1, blobCatalog1, catalogDb, new FlatDbConfig()))
        {
            repo.LoadFromCatalog();
            Snapshot snap = CreateTestSnapshot(s0, s1, TestItem.AddressA);
            repo.ConvertSnapshotToPersistedSnapshot(snap);
        }

        // Session 2: reload from disk
        using (ArenaManager smallArena2 = new(Path.Combine(_testDir, "arenas", "base"), 0, maxArenaSize: 4096))
        using (ArenaManager largeArena2 = new(Path.Combine(_testDir, "arenas", "compacted"), 0, maxArenaSize: 4096))
        using (BlobArenaCatalog blobCatalog2 = new(blobCatalogDb))
        using (BlobArenaManager smallBlobs2 = new(Path.Combine(_testDir, "blobs", "small"), 1024 * 1024, blobCatalog2, BlobArenaPool.Small))
        using (BlobArenaManager largeBlobs2 = new(Path.Combine(_testDir, "blobs", "large"), 1024 * 1024, blobCatalog2, BlobArenaPool.Large))
        using (PersistedSnapshotRepository repo = new(smallArena2, smallBlobs2, largeArena2, largeBlobs2, blobCatalog2, catalogDb, new FlatDbConfig()))
        {
            repo.LoadFromCatalog();
            Assert.That(repo.SnapshotCount, Is.EqualTo(1));
            Assert.That(repo.TryLeaseSnapshotTo(s1, out PersistedSnapshot? snapshot), Is.True);
            snapshot!.Dispose();
        }
    }

    [Test]
    public void PruneBefore_RemovesOldSnapshots()
    {
        using ArenaManager smallArena = new(Path.Combine(_testDir, "arenas", "base"), 0, maxArenaSize: 4096);
        using ArenaManager largeArena = new(Path.Combine(_testDir, "arenas", "compacted"), 0, maxArenaSize: 4096);
        using BlobArenaCatalog blobCatalog = new(new MemDb());
        using BlobArenaManager smallBlobs = new(Path.Combine(_testDir, "blobs", "small"), 1024 * 1024, blobCatalog, BlobArenaPool.Small);
        using BlobArenaManager largeBlobs = new(Path.Combine(_testDir, "blobs", "large"), 1024 * 1024, blobCatalog, BlobArenaPool.Large);
        using PersistedSnapshotRepository repo = new(smallArena, smallBlobs, largeArena, largeBlobs, blobCatalog, new MemDb(), new FlatDbConfig());
        repo.LoadFromCatalog();

        StateId s0 = new(0, Keccak.EmptyTreeHash);
        StateId s1 = new(1, Keccak.Compute("1"));
        StateId s2 = new(2, Keccak.Compute("2"));
        StateId s3 = new(3, Keccak.Compute("3"));

        Snapshot snap1 = CreateTestSnapshot(s0, s1, TestItem.AddressA);
        Snapshot snap2 = CreateTestSnapshot(s1, s2, TestItem.AddressB);
        Snapshot snap3 = CreateTestSnapshot(s2, s3, TestItem.AddressC);

        repo.ConvertSnapshotToPersistedSnapshot(snap1);
        repo.ConvertSnapshotToPersistedSnapshot(snap2);
        repo.ConvertSnapshotToPersistedSnapshot(snap3);
        Assert.That(repo.SnapshotCount, Is.EqualTo(3));

        // Prune before block 2 (removes snap1 with To=1)
        int pruned = repo.PruneBefore(new StateId(2, Keccak.Compute("prune")));
        Assert.That(pruned, Is.EqualTo(1));
        Assert.That(repo.SnapshotCount, Is.EqualTo(2));
    }
}
