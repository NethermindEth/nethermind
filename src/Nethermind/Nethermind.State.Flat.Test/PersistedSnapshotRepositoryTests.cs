// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State.Flat.Persistence.BloomFilter;
using Nethermind.State.Flat.PersistedSnapshots;
using Nethermind.State.Flat.PersistedSnapshots.Storage;
using Nethermind.Trie;
using NUnit.Framework;
using Nethermind.State.Flat.Hsst.BTree;

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
        using FlatTestContainer tier = new(arenaFileSizeBytes: 4096);
        SnapshotRepository repo = tier.Repository;

        StateId s0 = new(0, Keccak.EmptyTreeHash);
        StateId s1 = new(1, Keccak.Compute("1"));
        Snapshot snap = CreateTestSnapshot(s0, s1, TestItem.AddressA);

        tier.ConvertToPersistedBase(snap).Dispose();
        Assert.That(repo.PersistedSnapshotCount, Is.EqualTo(1));

        // Query through the snapshot
        Assert.That(repo.TryLeasePersistedState(s1, SnapshotTier.PersistedBase, out PersistedSnapshot? persisted), Is.True);
        Assert.That(persisted!.From, Is.EqualTo(s0));
        Assert.That(persisted.To, Is.EqualTo(s1));
        Assert.That(persisted.TryGetAccount(TestItem.AddressA, out Account? decoded), Is.True);
        Assert.That(decoded!.Balance, Is.EqualTo((UInt256)1000));
        persisted.Dispose();
    }

    /// <summary>
    /// Regression: an address with 256k sequential storage slots fills four fully-dense
    /// 30-byte slot-prefix groups (65536 slots each). The builder writes the per-address
    /// slot column through <c>ArenaBufferWriter</c> (see <see cref="SnapshotRepository"/>),
    /// and a full prefix group's inner sub-slot HSST exceeds that writer's 1 MiB buffer — so the
    /// single <c>HsstBTreeBuilder.Add</c> for the oversized prefix-group value must still round-trip.
    /// </summary>
    [Test]
    public void ConvertSnapshot_SequentialSlotsAcrossDensePrefixGroups_RoundTrips()
    {
        // 64 MiB shared arena: a 256k-slot snapshot (~10 MiB) stays below the 512 MiB
        // dedicated-arena threshold, so it must fit within a single shared arena file.
        using FlatTestContainer tier = new(arenaFileSizeBytes: 64 * 1024 * 1024, blobFileSizeBytes: 4 * 1024 * 1024);
        SnapshotRepository repo = tier.Repository;

        const int slotCount = 256 * 1024;
        SnapshotContent content = new();
        TestFixtureHelpers.AddSequentialSlots(content, TestItem.AddressA, firstSlot: 1, count: slotCount);

        StateId s0 = new(0, Keccak.EmptyTreeHash);
        StateId s1 = new(1, Keccak.Compute("seq-slots"));
        using PersistedSnapshot persisted = tier.ConvertToPersistedBase(
            new Snapshot(s0, s1, content, _pool, ResourcePool.Usage.MainBlockProcessing));

        // Probe slots spanning multiple prefix groups (group boundaries fall on multiples of 65536).
        foreach (int probe in new[] { 1, 65535, 65536, 131072, slotCount })
        {
            SlotValue slot = default;
            Assert.That(persisted.TryGetSlot(TestItem.AddressA, (UInt256)probe, ref slot), Is.True, $"slot {probe} missing");
            Assert.That(slot.AsReadOnlySpan.SequenceEqual(TestFixtureHelpers.SequentialSlotValue(probe)), Is.True,
                $"slot {probe} value mismatch");
        }
    }

    [Test]
    public void NewerSnapshot_OverridesOlderValue()
    {
        using FlatTestContainer tier = new(arenaFileSizeBytes: 4096);
        SnapshotRepository repo = tier.Repository;

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

        tier.ConvertToPersistedBase(snap1).Dispose();
        tier.ConvertToPersistedBase(snap2).Dispose();

        // The newest snapshot (s1→s2) should have rlp2 at the path
        Assert.That(repo.TryLeasePersistedState(s2, SnapshotTier.PersistedBase, out PersistedSnapshot? newest), Is.True);
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
        using (FlatTestContainer tier1 = new(arenaFileSizeBytes: 4096, baseDbPath: _testDir, catalogDb: catalogDb))
        {
            SnapshotRepository repo = tier1.Repository;
            Snapshot snap = CreateTestSnapshot(s0, s1, TestItem.AddressA);
            tier1.ConvertToPersistedBase(snap).Dispose();
        }

        // Session 2: reload from disk
        using (FlatTestContainer tier2 = new(arenaFileSizeBytes: 4096, baseDbPath: _testDir, catalogDb: catalogDb))
        {
            SnapshotRepository repo = tier2.Repository;
            Assert.That(repo.PersistedSnapshotCount, Is.EqualTo(1));
            Assert.That(repo.TryLeasePersistedState(s1, SnapshotTier.PersistedBase, out PersistedSnapshot? snapshot), Is.True);
            snapshot!.Dispose();
        }
    }

    [Test]
    public void ConvertSnapshot_RoundTrip_AllDataCategories()
    {
        using FlatTestContainer tier = new(arenaFileSizeBytes: 4096);
        SnapshotRepository repo = tier.Repository;

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

        tier.ConvertToPersistedBase(snap).Dispose();

        Assert.That(repo.TryLeasePersistedState(s1, SnapshotTier.PersistedBase, out PersistedSnapshot? persisted), Is.True);
        using PersistedSnapshot _ = persisted!;

        // 1. Account
        Assert.That(persisted!.TryGetAccount(acctAddr, out Account? account), Is.True);
        Assert.That(account, Is.Not.Null);
        Assert.That(account!.Balance, Is.EqualTo((UInt256)500));

        // 2. Storage slot
        SlotValue readSlot = default;
        Assert.That(persisted.TryGetSlot(storageAddr, slotIndex, ref readSlot), Is.True);
        Assert.That(readSlot.AsReadOnlySpan.ToArray(), Is.EqualTo(slotBytes));

        // 3. Self-destruct flag
        Assert.That(persisted.TryGetSelfDestructFlag(selfDestructAddr), Is.Not.Null);

        // 4. State trie node
        Assert.That(persisted.TryLoadStateNodeRlp(statePath, out byte[]? stateResult), Is.True);
        Assert.That(stateResult, Is.EqualTo(stateRlp));

        // 5. Storage trie node
        Assert.That(persisted.TryLoadStorageNodeRlp(storageTrieAddr.ValueHash256, storagePath, out byte[]? storageResult), Is.True);
        Assert.That(storageResult, Is.EqualTo(storageRlp));
    }

    [Test]
    public void RemoveStatesUntil_RemovesOldSnapshots()
    {
        using FlatTestContainer tier = new(arenaFileSizeBytes: 4096);
        SnapshotRepository repo = tier.Repository;

        StateId s0 = new(0, Keccak.EmptyTreeHash);
        StateId s1 = new(1, Keccak.Compute("1"));
        StateId s2 = new(2, Keccak.Compute("2"));
        StateId s3 = new(3, Keccak.Compute("3"));

        Snapshot snap1 = CreateTestSnapshot(s0, s1, TestItem.AddressA);
        Snapshot snap2 = CreateTestSnapshot(s1, s2, TestItem.AddressB);
        Snapshot snap3 = CreateTestSnapshot(s2, s3, TestItem.AddressC);

        tier.ConvertToPersistedBase(snap1).Dispose();
        tier.ConvertToPersistedBase(snap2).Dispose();
        tier.ConvertToPersistedBase(snap3).Dispose();
        Assert.That(repo.PersistedSnapshotCount, Is.EqualTo(3));

        // Remove states until block 2 (removes snap1 with To=1)
        repo.RemovePersistedStatesUntil(2);
        Assert.That(repo.PersistedSnapshotCount, Is.EqualTo(2));
    }

    [TestCase(100)]
    [TestCase(1000)]
    public void ManyBaseSnapshots_ShareUnderlyingFiles(int count)
    {
        // Regression for the old "Blob arena id space exhausted (65535 arenas per tier)"
        // bug: ids were minted per base-conversion call, so 65k base
        // snapshots used 65k blob arena ids. Per-file ids pack many writers into one file —
        // file count stays bounded under steady state.
        using FlatTestContainer tier = new(arenaFileSizeBytes: 64 * 1024);
        SnapshotRepository repo = tier.Repository;

        StateId prev = new(0, Keccak.EmptyTreeHash);
        for (int i = 1; i <= count; i++)
        {
            StateId next = new(i, Keccak.Compute($"s{i}"));
            Snapshot snap = CreateTestSnapshot(prev, next, TestItem.Addresses[i % TestItem.Addresses.Length]);
            tier.ConvertToPersistedBase(snap).Dispose();
            prev = next;
        }

        Assert.That(repo.PersistedSnapshotCount, Is.EqualTo(count));
        // Files stay packed: bounded by max file size / typical write size, not by snapshot count.
        int blobFileCount = Directory.GetFiles(Path.Combine(tier.BaseDbPath, "persisted_snapshot", "blob"), "blob_*.bin").Length;
        Assert.That(blobFileCount, Is.LessThan(count),
            "expected many base snapshots to share blob arena files");
    }

    [TestCase(true, TestName = "ConvertSnapshot_RecordsBlobRange(with trie nodes)")]
    [TestCase(false, TestName = "ConvertSnapshot_RecordsBlobRange(no trie nodes)")]
    public void ConvertSnapshot_RecordsBlobRange(bool withTrieNode)
    {
        using FlatTestContainer tier = new(arenaFileSizeBytes: 64 * 1024);
        SnapshotRepository repo = tier.Repository;

        StateId s0 = new(0, Keccak.EmptyTreeHash);
        StateId s1 = new(1, Keccak.Compute("1"));
        SnapshotContent content = new();
        content.Accounts[TestItem.AddressA] = Build.An.Account.WithBalance(1000).TestObject;
        if (withTrieNode)
            content.StateNodes[new TreePath(Keccak.Compute("p"), 4)] = new TrieNode(NodeType.Leaf, [0xC2, 0x80, 0x80]);

        using PersistedSnapshot persisted = tier.ConvertToPersistedBase(
            new Snapshot(s0, s1, content, _pool, ResourcePool.Usage.MainBlockProcessing));

        if (withTrieNode)
        {
            Assert.That(persisted.BlobRange.IsEmpty, Is.False, "a base snapshot with trie nodes records a non-empty blob range");
            Assert.That(persisted.BlobRange.Length, Is.GreaterThan(0));
        }
        else
        {
            Assert.That(persisted.BlobRange.IsEmpty, Is.True, "a base snapshot with no trie nodes has no blob region");
        }
    }

    [TestCase(true, TestName = "BlobRange_SurvivesReloadViaMetadata(with trie nodes)")]
    [TestCase(false, TestName = "BlobRange_SurvivesReloadViaMetadata(no trie nodes)")]
    public void BlobRange_SurvivesReloadViaMetadata(bool withTrieNode)
    {
        // The blob range lives in the snapshot's own metadata HSST (blob_range key), not the
        // catalog, so it must round-trip a restart: read back by the PersistedSnapshot ctor.
        MemDb catalogDb = new();

        StateId s0 = new(0, Keccak.EmptyTreeHash);
        StateId s1 = new(1, Keccak.Compute("1"));

        using (FlatTestContainer tier1 = new(arenaFileSizeBytes: 64 * 1024, baseDbPath: _testDir, catalogDb: catalogDb))
        {
            SnapshotRepository repo1 = tier1.Repository;
            SnapshotContent content = new();
            content.Accounts[TestItem.AddressA] = Build.An.Account.WithBalance(1000).TestObject;
            if (withTrieNode)
                content.StateNodes[new TreePath(Keccak.Compute("p"), 4)] = new TrieNode(NodeType.Leaf, [0xC2, 0x80, 0x80]);
            tier1.ConvertToPersistedBase(
                new Snapshot(s0, s1, content, _pool, ResourcePool.Usage.MainBlockProcessing)).Dispose();
        }

        using FlatTestContainer tier2 = new(arenaFileSizeBytes: 64 * 1024, baseDbPath: _testDir, catalogDb: catalogDb);
        SnapshotRepository repo2 = tier2.Repository;

        Assert.That(repo2.TryLeasePersistedState(s1, SnapshotTier.PersistedBase, out PersistedSnapshot? reloaded), Is.True);
        using (reloaded)
            Assert.That(reloaded!.BlobRange.IsEmpty, Is.EqualTo(!withTrieNode),
                "the base's blob range must round-trip a restart via its metadata HSST");
    }

    [Test]
    public void LeaseBaseSnapshotsInRange_ReturnsBasesTilingWindow()
    {
        using FlatTestContainer tier = new(arenaFileSizeBytes: 64 * 1024);
        SnapshotRepository repo = tier.Repository;

        StateId[] ids = new StateId[4];
        ids[0] = new(0, Keccak.EmptyTreeHash);
        for (int i = 1; i < 4; i++)
        {
            ids[i] = new(i, Keccak.Compute($"s{i}"));
            tier.ConvertToPersistedBase(
                CreateTestSnapshot(ids[i - 1], ids[i], TestItem.Addresses[i])).Dispose();
        }

        using PersistedSnapshotList bases = repo.LeaseBaseSnapshotsInRange(ids[0], ids[3]);
        Assert.That(bases.Count, Is.EqualTo(3));
        // Walk-back order: newest first.
        Assert.That(bases[0].To, Is.EqualTo(ids[3]));
        Assert.That(bases[^1].From, Is.EqualTo(ids[0]));
    }

    /// <summary>
    /// Regression for the ReconstructBloom pass inside LoadFromCatalog: after a restart,
    /// every loaded snapshot must carry its own real bloom (built from its on-disk image),
    /// not the AlwaysTrue placeholder it was constructed with. The persistable covering
    /// (0, 4] holds every address written across the four bases; each base holds its own.
    /// </summary>
    [Test]
    public void LoadFromCatalog_ReconstructsBloom_PerSnapshot()
    {
        StateId[] ids = new StateId[5];
        ids[0] = new(0, Keccak.EmptyTreeHash);
        for (int i = 1; i <= 4; i++) ids[i] = new(i, Keccak.Compute($"s{i}"));

        MemDb catalogDb = new();

        // Session 1: 4 bases + a CompactSize=4 persistable covering all 4 of them.
        using (FlatTestContainer tier1 = new(
            arenaFileSizeBytes: 64 * 1024, baseDbPath: _testDir, catalogDb: catalogDb,
            configure: b => b.AddSingleton<ICompactionSchedule>(ScheduleHelper.CreateWithOffset(new FlatDbConfig { CompactSize = 4 }, 0))))
        {
            SnapshotRepository repo = tier1.Repository;
            for (int i = 1; i <= 4; i++)
                tier1.ConvertToPersistedBase(
                    CreateTestSnapshot(ids[i - 1], ids[i], TestItem.Addresses[i - 1])).Dispose();

            tier1.Compactor.DoCompactPersistable(ids[4]);  // persistable at To=4 covering (0, 4]
        }

        // Session 2: reload. LoadFromCatalog now auto-calls ReconstructBloom.
        using FlatTestContainer tier2 = new(arenaFileSizeBytes: 64 * 1024, baseDbPath: _testDir, catalogDb: catalogDb);
        SnapshotRepository repo2 = tier2.Repository;

        // With the v7 (To, depth)-keyed catalog the base at ids[4] survives alongside the
        // persistable at the same To — both buckets must lease independently.
        Assert.That(repo2.TryLeasePersistedState(ids[4], SnapshotTier.PersistedPersistable, out PersistedSnapshot? persistableAt4), Is.True);
        using (persistableAt4)
        {
            // The persistable's bloom is built from its own merged HSST — it covers (0, 4]
            // and therefore holds every address written across the four bases.
            BloomFilter persistableBloom = persistableAt4!.Bloom;
            Assert.That(persistableBloom.Count, Is.GreaterThan(0),
                "ReconstructBloom must have built a real bloom for the persistable");
            Assert.That(persistableAt4.From.BlockNumber, Is.EqualTo(0));
            Assert.That(persistableAt4.To.BlockNumber, Is.EqualTo(4));
            for (int i = 1; i <= 4; i++)
            {
                ulong key = PersistedSnapshotBloomBuilder.AddressKey(TestItem.Addresses[i - 1]);
                Assert.That(persistableBloom.MightContain(key), Is.True,
                    $"AddressKey for base {i} must be in the persistable's merged bloom");
            }
        }

        // Each base also carries its own real bloom built from its single address.
        for (int i = 1; i <= 4; i++)
        {
            Assert.That(repo2.TryLeasePersistedState(ids[i], SnapshotTier.PersistedBase, out PersistedSnapshot? baseAt), Is.True,
                $"base at ids[{i}] must round-trip under v7");
            using (baseAt)
            {
                Assert.That(baseAt!.Bloom.Count, Is.GreaterThan(0),
                    $"ReconstructBloom must have built a real bloom for base {i}");
                ulong key = PersistedSnapshotBloomBuilder.AddressKey(TestItem.Addresses[i - 1]);
                Assert.That(baseAt.Bloom.MightContain(key), Is.True,
                    $"base {i}'s own address must be in its bloom");
            }
        }
    }

    /// <summary>
    /// Regression for the v7 (To, depth)-keyed catalog: before v7, a persistable at the
    /// same To as a base overwrote the base's catalog entry, so a restart would lose the
    /// base. With v7 both round-trip independently — SnapshotCount on reload equals the
    /// number of <c>Add</c> calls in the prior session.
    /// </summary>
    [Test]
    public void LoadFromCatalog_RoundTripsBaseAndPersistableAtSameTo()
    {
        StateId[] ids = new StateId[5];
        ids[0] = new(0, Keccak.EmptyTreeHash);
        for (int i = 1; i <= 4; i++) ids[i] = new(i, Keccak.Compute($"s{i}"));

        MemDb catalogDb = new();

        using (FlatTestContainer tier1 = new(
            arenaFileSizeBytes: 64 * 1024, baseDbPath: _testDir, catalogDb: catalogDb,
            configure: b => b.AddSingleton<ICompactionSchedule>(ScheduleHelper.CreateWithOffset(new FlatDbConfig { CompactSize = 4 }, 0))))
        {
            SnapshotRepository repo = tier1.Repository;
            for (int i = 1; i <= 4; i++)
                tier1.ConvertToPersistedBase(
                    CreateTestSnapshot(ids[i - 1], ids[i], TestItem.Addresses[i - 1])).Dispose();

            tier1.Compactor.DoCompactPersistable(ids[4]);

            Assert.That(repo.PersistedSnapshotCount, Is.EqualTo(5), "session 1 must hold 4 bases + 1 persistable");
        }

        using FlatTestContainer tier2 = new(arenaFileSizeBytes: 64 * 1024, baseDbPath: _testDir, catalogDb: catalogDb);
        SnapshotRepository repo2 = tier2.Repository;

        Assert.That(repo2.PersistedSnapshotCount, Is.EqualTo(5),
            "all five snapshots (4 bases + 1 persistable at the last base's To) must round-trip under v7");
        for (int i = 1; i <= 4; i++)
        {
            Assert.That(repo2.TryLeasePersistedState(ids[i], SnapshotTier.PersistedBase, out PersistedSnapshot? b), Is.True,
                $"base at ids[{i}] must survive reload");
            b!.Dispose();
        }
        Assert.That(repo2.TryLeasePersistedState(ids[4], SnapshotTier.PersistedPersistable, out PersistedSnapshot? persistable), Is.True);
        persistable!.Dispose();
    }

    /// <summary>
    /// Exercise the parallel-then-serial split in <c>LoadFromCatalog</c>: build enough
    /// snapshots in session 1 to spread across multiple <see cref="System.Threading.Tasks.Parallel.ForEach"/>
    /// partitions, reload in session 2, and verify the parallel construction + serial
    /// sorted-set rebuild preserves: snapshot count, per-bucket leasability, ordered-id
    /// invariants (the From/To chain reachable via <c>LeaseBaseSnapshotsInRange</c>), and the
    /// ReconstructBloom end-state (every loaded snapshot carries its own real bloom).
    /// Stays below <c>ParallelLoadThreshold</c> so the progress logger is bypassed —
    /// that codepath is a one-line gate we trust by inspection.
    /// </summary>
    [Test]
    public void LoadFromCatalog_Parallel_PreservesOrderingAndDicts()
    {
        const int N = 32;
        StateId[] ids = new StateId[N + 1];
        ids[0] = new(0, Keccak.EmptyTreeHash);
        for (int i = 1; i <= N; i++) ids[i] = new(i, Keccak.Compute($"s{i}"));

        MemDb catalogDb = new();

        using (FlatTestContainer tier1 = new(
            arenaFileSizeBytes: 64 * 1024, baseDbPath: _testDir, catalogDb: catalogDb,
            configure: b => b.AddSingleton<ICompactionSchedule>(ScheduleHelper.CreateWithOffset(new FlatDbConfig { CompactSize = 8 }, 0))))
        {
            SnapshotRepository repo = tier1.Repository;
            for (int i = 1; i <= N; i++)
                tier1.ConvertToPersistedBase(
                    CreateTestSnapshot(ids[i - 1], ids[i], TestItem.Addresses[(i - 1) % TestItem.Addresses.Length])).Dispose();

            // Throw in two persistables (CompactSize=8) at boundaries 8 and 16 so the
            // catalog has multi-bucket entries that exercise the bucket-routing branch
            // in the parallel LoadSnapshot.
            tier1.Compactor.DoCompactPersistable(ids[8]);
            tier1.Compactor.DoCompactPersistable(ids[16]);
        }

        using FlatTestContainer tier2 = new(arenaFileSizeBytes: 64 * 1024, baseDbPath: _testDir, catalogDb: catalogDb);
        SnapshotRepository repo2 = tier2.Repository;

        // All N bases + 2 persistables survive.
        Assert.That(repo2.PersistedSnapshotCount, Is.EqualTo(N + 2));
        for (int i = 1; i <= N; i++)
        {
            Assert.That(repo2.TryLeasePersistedState(ids[i], SnapshotTier.PersistedBase, out PersistedSnapshot? b), Is.True, $"base ids[{i}] missing");
            b!.Dispose();
        }
        Assert.That(repo2.TryLeasePersistedState(ids[8], SnapshotTier.PersistedPersistable, out PersistedSnapshot? p8), Is.True);
        p8!.Dispose();
        Assert.That(repo2.TryLeasePersistedState(ids[16], SnapshotTier.PersistedPersistable, out PersistedSnapshot? p16), Is.True);
        p16!.Dispose();

        // Ordered-id invariant: the bases tile the whole (0, N] window via their From chain.
        // Catches a missing or mis-routed sorted-set entry.
        using (PersistedSnapshotList chain = repo2.LeaseBaseSnapshotsInRange(ids[0], ids[N]))
            Assert.That(chain.Count, Is.EqualTo(N), "every base must be reachable via the From chain");

        // Bloom end-state: ReconstructBloom builds a real per-snapshot bloom for the base at
        // ids[1] and for the persistable covering (0, 8].
        Assert.That(repo2.TryLeasePersistedState(ids[1], SnapshotTier.PersistedBase, out PersistedSnapshot? baseAt1), Is.True);
        using (baseAt1)
            Assert.That(baseAt1!.Bloom.Count, Is.GreaterThan(0), "base ids[1] must have a real bloom");
        Assert.That(repo2.TryLeasePersistedState(ids[8], SnapshotTier.PersistedPersistable, out PersistedSnapshot? persistableAt8), Is.True);
        using (persistableAt8)
            Assert.That(persistableAt8!.Bloom.Count, Is.GreaterThan(0), "persistable at ids[8] must have a real bloom");
    }

    // With bloom disabled (bits-per-key 0) the loader's Convert path uses the AlwaysTrue
    // sentinel and ReconstructBloom returns early on restart — data must still survive.
    [Test]
    public void LoadFromCatalog_BloomDisabled_SkipsReconstructionButDataSurvives()
    {
        StateId s0 = new(0, Keccak.EmptyTreeHash);
        StateId s1 = new(1, Keccak.Compute("nb1"));
        MemDb catalogDb = new();

        using (FlatTestContainer tier1 = new(
            config: new FlatDbConfig { PersistedSnapshotBloomBitsPerKey = 0 },
            arenaFileSizeBytes: 64 * 1024, baseDbPath: _testDir, catalogDb: catalogDb))
        {
            tier1.ConvertToPersistedBase(CreateTestSnapshot(s0, s1, TestItem.AddressA)).Dispose();
        }

        using FlatTestContainer tier2 = new(
            config: new FlatDbConfig { PersistedSnapshotBloomBitsPerKey = 0 },
            arenaFileSizeBytes: 64 * 1024, baseDbPath: _testDir, catalogDb: catalogDb);

        Assert.That(tier2.Repository.TryLeasePersistedState(s1, SnapshotTier.PersistedBase, out PersistedSnapshot? p), Is.True);
        using (p)
        {
            Assert.That(p!.Bloom.Count, Is.EqualTo(0), "bloom disabled → AlwaysTrue sentinel, no reconstruction");
            Assert.That(p.TryGetAccount(TestItem.AddressA, out _), Is.True, "data must survive restart with bloom disabled");
        }
    }

    // With validation enabled, Convert runs PersistedSnapshotUtils.ValidatePersistedSnapshot
    // on the freshly written base; a valid snapshot must convert and round-trip without throwing.
    [Test]
    public void ConvertToPersistedBase_WithValidationEnabled_RoundTrips()
    {
        StateId s0 = new(0, Keccak.EmptyTreeHash);
        StateId s1 = new(1, Keccak.Compute("val1"));

        using FlatTestContainer tier = new(
            config: new FlatDbConfig { ValidatePersistedSnapshot = true },
            arenaFileSizeBytes: 64 * 1024, baseDbPath: _testDir);

        using PersistedSnapshot p = tier.ConvertToPersistedBase(CreateTestSnapshot(s0, s1, TestItem.AddressA, 77));
        Assert.That(p.TryGetAccount(TestItem.AddressA, out Account? acc), Is.True);
        Assert.That(acc!.Balance, Is.EqualTo((UInt256)77));
    }

    // A converted base records a contiguous trie-RLP blob run, so its blob-range advise calls
    // hit the non-empty fadvise branch (a no-op against the test arena, but must not throw).
    [Test]
    public void AdviseBlobRange_OnConvertedBaseWithTrieNodes_DoesNotThrow()
    {
        StateId s0 = new(0, Keccak.EmptyTreeHash);
        StateId s1 = new(1, Keccak.Compute("blob1"));
        using FlatTestContainer tier = new(arenaFileSizeBytes: 64 * 1024, baseDbPath: _testDir);

        SnapshotContent content = new();
        Nethermind.Trie.TreePath path = new(Keccak.Compute("bp"), 4);
        content.StateNodes[path] = new Nethermind.Trie.TrieNode(Nethermind.Trie.NodeType.Leaf, [0xC2, 0x80, 0x80]);
        using PersistedSnapshot p = tier.ConvertToPersistedBase(
            new Snapshot(s0, s1, content, _pool, ResourcePool.Usage.MainBlockProcessing));

        Assert.DoesNotThrow(() => p.AdviseWillNeedBlobRange());
        Assert.DoesNotThrow(() => p.AdviseDontNeedBlobRange());
        Assert.That(p.TryLoadStateNodeRlp(path, out _), Is.True);
    }

    // End-to-end-ish read-through: a base converted with a REAL bloom (default config),
    // wrapped in a PersistedSnapshotStack, resolves a present account/slot and skips absent
    // addresses — exercising the stack's real-bloom gate (MightContain == false → continue).
    [Test]
    public void Stack_RealBloom_AdmitsPresentSkipsAbsentAddresses()
    {
        StateId s0 = new(0, Keccak.EmptyTreeHash);
        StateId s1 = new(1, Keccak.Compute("rb1"));
        using FlatTestContainer tier = new(arenaFileSizeBytes: 64 * 1024, baseDbPath: _testDir);

        SnapshotContent content = new();
        content.Accounts[TestItem.AddressA] = Build.An.Account.WithBalance(123).TestObject;
        byte[] slot = new byte[32]; slot[31] = 0x55;
        content.Storages[(TestItem.AddressA, (UInt256)1)] = new SlotValue(slot);
        PersistedSnapshot persisted = tier.ConvertToPersistedBase(
            new Snapshot(s0, s1, content, _pool, ResourcePool.Usage.MainBlockProcessing));

        PersistedSnapshotList list = new(1) { persisted };
        using PersistedSnapshotStack stack = new(list, recordDetailedMetrics: false);

        Assert.That(stack.TryGetAccount(TestItem.AddressA, out Account? a), Is.True);
        Assert.That(a!.Balance, Is.EqualTo((UInt256)123));
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        Assert.That(stack.TryGetSlot(TestItem.AddressA, (UInt256)1, -1, start, out byte[]? sv), Is.True);
        Assert.That(sv![^1], Is.EqualTo((byte)0x55));

        // Absent addresses: the real bloom excludes them (or the snapshot misses) → fall through.
        foreach (Address absent in new[] { TestItem.AddressB, TestItem.AddressC, TestItem.AddressD, TestItem.AddressE, TestItem.AddressF })
            Assert.That(stack.TryGetAccount(absent, out _), Is.False, $"{absent} must not resolve");
    }
}
