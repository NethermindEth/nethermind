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
        using BlobArenaManager smallBlobs = new(Path.Combine(_testDir, "blobs", "small"), 1024 * 1024, ArenaReservationTags.BlobSmall);
        using PersistedSnapshotRepository repo = new(smallArena, smallBlobs, new MemDb(), new FlatDbConfig(), new PersistedSnapshotBloomFilterManager());
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
        using BlobArenaManager smallBlobs = new(Path.Combine(_testDir, "blobs", "small"), 1024 * 1024, ArenaReservationTags.BlobSmall);
        using PersistedSnapshotRepository repo = new(smallArena, smallBlobs, new MemDb(), new FlatDbConfig(), new PersistedSnapshotBloomFilterManager());
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

        // Session 1: persist a snapshot
        using (ArenaManager smallArena1 = new(Path.Combine(_testDir, "arenas", "base"), 0, maxArenaSize: 4096))
        using (BlobArenaManager smallBlobs1 = new(Path.Combine(_testDir, "blobs", "small"), 1024 * 1024, ArenaReservationTags.BlobSmall))
        using (PersistedSnapshotRepository repo = new(smallArena1, smallBlobs1, catalogDb, new FlatDbConfig(), new PersistedSnapshotBloomFilterManager()))
        {
            repo.LoadFromCatalog();
            Snapshot snap = CreateTestSnapshot(s0, s1, TestItem.AddressA);
            repo.ConvertSnapshotToPersistedSnapshot(snap);
        }

        // Session 2: reload from disk
        using (ArenaManager smallArena2 = new(Path.Combine(_testDir, "arenas", "base"), 0, maxArenaSize: 4096))
        using (BlobArenaManager smallBlobs2 = new(Path.Combine(_testDir, "blobs", "small"), 1024 * 1024, ArenaReservationTags.BlobSmall))
        using (PersistedSnapshotRepository repo = new(smallArena2, smallBlobs2, catalogDb, new FlatDbConfig(), new PersistedSnapshotBloomFilterManager()))
        {
            repo.LoadFromCatalog();
            Assert.That(repo.SnapshotCount, Is.EqualTo(1));
            Assert.That(repo.TryLeaseSnapshotTo(s1, out PersistedSnapshot? snapshot), Is.True);
            snapshot!.Dispose();
        }
    }

    [Test]
    public void ConvertSnapshot_RoundTrip_AllDataCategories()
    {
        using ArenaManager smallArena = new(Path.Combine(_testDir, "arenas", "base"), 0, maxArenaSize: 4096);
        using BlobArenaManager smallBlobs = new(Path.Combine(_testDir, "blobs", "small"), 1024 * 1024, ArenaReservationTags.BlobSmall);
        using PersistedSnapshotRepository repo = new(smallArena, smallBlobs, new MemDb(), new FlatDbConfig(), new PersistedSnapshotBloomFilterManager());
        repo.LoadFromCatalog();

        StateId s0 = new(0, Keccak.EmptyTreeHash);
        StateId s1 = new(1, Keccak.Compute("1"));

        Address acctAddr = TestItem.AddressA;
        Address selfDestructAddr = TestItem.AddressB;
        Address storageAddr = TestItem.AddressC;
        UInt256 slotIndex = (UInt256)42;
        byte[] slotBytes = new byte[32];
        slotBytes[31] = 0xAB;
        slotBytes[30] = 0xCD;
        SlotValue slotValue = new(slotBytes);

        TreePath statePath = new(Keccak.Compute("state_path"), 4);
        byte[] stateRlp = [0xC2, 0x80, 0x80];
        Hash256 storageTrieAddr = Keccak.Compute("storage_trie_addr");
        TreePath storagePath = new(Keccak.Compute("storage_path"), 6);
        byte[] storageRlp = [0xC1, 0x80];

        SnapshotContent content = new();
        content.Accounts[acctAddr] = Build.An.Account.WithBalance(500).TestObject;
        content.Storages[(storageAddr, slotIndex)] = slotValue;
        content.SelfDestructedStorageAddresses[selfDestructAddr] = false;
        content.StateNodes[statePath] = new TrieNode(NodeType.Leaf, stateRlp);
        content.StorageNodes[(storageTrieAddr, storagePath)] = new TrieNode(NodeType.Branch, storageRlp);
        Snapshot snap = new(s0, s1, content, _pool, ResourcePool.Usage.MainBlockProcessing);

        repo.ConvertSnapshotToPersistedSnapshot(snap);

        Assert.That(repo.TryLeaseSnapshotTo(s1, out PersistedSnapshot? persisted), Is.True);
        using PersistedSnapshot _ = persisted!;

        // 1. Account
        Assert.That(persisted!.TryGetAccount(ValueKeccak.Compute(acctAddr.Bytes), out Account? account), Is.True);
        Assert.That(account, Is.Not.Null);
        Assert.That(account!.Balance, Is.EqualTo((UInt256)500));

        // 2. Storage slot
        SlotValue readSlot = default;
        Assert.That(persisted.TryGetSlot(ValueKeccak.Compute(storageAddr.Bytes), slotIndex, ref readSlot), Is.True);
        Assert.That(readSlot.AsReadOnlySpan.ToArray(), Is.EqualTo(slotBytes));

        // 3. Self-destruct flag
        Assert.That(persisted.IsSelfDestructed(ValueKeccak.Compute(selfDestructAddr.Bytes)), Is.True);

        // 4. State trie node
        Assert.That(persisted.TryLoadStateNodeRlp(statePath, out byte[]? stateResult), Is.True);
        Assert.That(stateResult, Is.EqualTo(stateRlp));

        // 5. Storage trie node
        Assert.That(persisted.TryLoadStorageNodeRlp(storageTrieAddr.ValueHash256, storagePath, out byte[]? storageResult), Is.True);
        Assert.That(storageResult, Is.EqualTo(storageRlp));
    }

    [Test]
    public void PruneBefore_RemovesOldSnapshots()
    {
        using ArenaManager smallArena = new(Path.Combine(_testDir, "arenas", "base"), 0, maxArenaSize: 4096);
        using BlobArenaManager smallBlobs = new(Path.Combine(_testDir, "blobs", "small"), 1024 * 1024, ArenaReservationTags.BlobSmall);
        using PersistedSnapshotRepository repo = new(smallArena, smallBlobs, new MemDb(), new FlatDbConfig(), new PersistedSnapshotBloomFilterManager());
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

    [TestCase(100)]
    [TestCase(1000)]
    public void ManyBaseSnapshots_ShareUnderlyingFiles(int count)
    {
        // Regression for the old "Blob arena id space exhausted (65535 arenas per tier)"
        // bug: ids were minted per ConvertSnapshotToPersistedSnapshot call, so 65k base
        // snapshots used 65k blob arena ids. Per-file ids pack many writers into one file —
        // file count stays bounded under steady state.
        using ArenaManager smallArena = new(Path.Combine(_testDir, "arenas", "base"), 0, maxArenaSize: 64 * 1024);
        using BlobArenaManager smallBlobs = new(Path.Combine(_testDir, "blobs", "small"), 1024 * 1024, ArenaReservationTags.BlobSmall);
        using PersistedSnapshotRepository repo = new(smallArena, smallBlobs, new MemDb(), new FlatDbConfig(), new PersistedSnapshotBloomFilterManager());
        repo.LoadFromCatalog();

        StateId prev = new(0, Keccak.EmptyTreeHash);
        for (int i = 1; i <= count; i++)
        {
            StateId next = new(i, Keccak.Compute($"s{i}"));
            Snapshot snap = CreateTestSnapshot(prev, next, TestItem.Addresses[i % TestItem.Addresses.Length]);
            repo.ConvertSnapshotToPersistedSnapshot(snap);
            prev = next;
        }

        Assert.That(repo.SnapshotCount, Is.EqualTo(count));
        // Files stay packed: bounded by max file size / typical write size, not by snapshot count.
        int blobFileCount = Directory.GetFiles(Path.Combine(_testDir, "blobs", "small"), "blob_*.bin").Length;
        Assert.That(blobFileCount, Is.LessThan(count),
            "expected many base snapshots to share blob arena files");
    }
}
