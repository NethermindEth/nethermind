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
        using ArenaManager smallArena = new(Path.Combine(_testDir, "arenas", "base"), 0, maxArenaSize: 4096);
        using BlobArenaManager smallBlobs = new(Path.Combine(_testDir, "blobs", "small"), 1024 * 1024, PersistedSnapshotTier.Persisted);
        using PersistedSnapshotRepository repo = new(smallArena, smallBlobs, new MemDb(), new FlatDbConfig(), new PersistedSnapshotBloomFilterManager(), LimboLogs.Instance);
        repo.LoadFromCatalog();

        StateId s0 = new(0, Keccak.EmptyTreeHash);
        StateId s1 = new(1, Keccak.Compute("1"));
        Snapshot snap = CreateTestSnapshot(s0, s1, TestItem.AddressA);

        repo.ConvertSnapshotToPersistedSnapshot(snap).Dispose();
        Assert.That(repo.SnapshotCount, Is.EqualTo(1));

        // Query through the snapshot
        Assert.That(repo.TryLeaseSnapshotTo(s1, out PersistedSnapshot? persisted), Is.True);
        Assert.That(persisted!.From, Is.EqualTo(s0));
        Assert.That(persisted.To, Is.EqualTo(s1));
        Assert.That(persisted.TryGetAccount(TestItem.AddressA, out Account? decoded), Is.True);
        Assert.That(decoded!.Balance, Is.EqualTo((UInt256)1000));
        persisted.Dispose();
    }

    /// <summary>
    /// Regression: an address with 256k sequential storage slots fills four fully-dense
    /// 30-byte slot-prefix groups (65536 slots each). The builder writes the per-address
    /// slot column through <c>ArenaBufferWriter</c> (see <see cref="PersistedSnapshotRepository"/>),
    /// and a full prefix group's inner sub-slot HSST exceeds that writer's 1 MiB buffer — so the
    /// single <c>HsstBTreeBuilder.Add</c> for the oversized prefix-group value must still round-trip.
    /// </summary>
    [Test]
    public void ConvertSnapshot_SequentialSlotsAcrossDensePrefixGroups_RoundTrips()
    {
        // 64 MiB shared arena: a 256k-slot snapshot (~10 MiB) stays below the 512 MiB
        // dedicated-arena threshold, so it must fit within a single shared arena file.
        using ArenaManager smallArena = new(Path.Combine(_testDir, "arenas", "base"), 0, maxArenaSize: 64 * 1024 * 1024);
        using BlobArenaManager smallBlobs = new(Path.Combine(_testDir, "blobs", "small"), 4 * 1024 * 1024, PersistedSnapshotTier.Persisted);
        using PersistedSnapshotRepository repo = new(smallArena, smallBlobs, new MemDb(), new FlatDbConfig(), new PersistedSnapshotBloomFilterManager(), LimboLogs.Instance);
        repo.LoadFromCatalog();

        const int slotCount = 256 * 1024;
        SnapshotContent content = new();
        TestFixtureHelpers.AddSequentialSlots(content, TestItem.AddressA, firstSlot: 1, count: slotCount);

        StateId s0 = new(0, Keccak.EmptyTreeHash);
        StateId s1 = new(1, Keccak.Compute("seq-slots"));
        using PersistedSnapshot persisted = repo.ConvertSnapshotToPersistedSnapshot(
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
        using ArenaManager smallArena = new(Path.Combine(_testDir, "arenas", "base"), 0, maxArenaSize: 4096);
        using BlobArenaManager smallBlobs = new(Path.Combine(_testDir, "blobs", "small"), 1024 * 1024, PersistedSnapshotTier.Persisted);
        using PersistedSnapshotRepository repo = new(smallArena, smallBlobs, new MemDb(), new FlatDbConfig(), new PersistedSnapshotBloomFilterManager(), LimboLogs.Instance);
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

        repo.ConvertSnapshotToPersistedSnapshot(snap1).Dispose();
        repo.ConvertSnapshotToPersistedSnapshot(snap2).Dispose();

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
        using (BlobArenaManager smallBlobs1 = new(Path.Combine(_testDir, "blobs", "small"), 1024 * 1024, PersistedSnapshotTier.Persisted))
        using (PersistedSnapshotRepository repo = new(smallArena1, smallBlobs1, catalogDb, new FlatDbConfig(), new PersistedSnapshotBloomFilterManager(), LimboLogs.Instance))
        {
            repo.LoadFromCatalog();
            Snapshot snap = CreateTestSnapshot(s0, s1, TestItem.AddressA);
            repo.ConvertSnapshotToPersistedSnapshot(snap).Dispose();
        }

        // Session 2: reload from disk
        using (ArenaManager smallArena2 = new(Path.Combine(_testDir, "arenas", "base"), 0, maxArenaSize: 4096))
        using (BlobArenaManager smallBlobs2 = new(Path.Combine(_testDir, "blobs", "small"), 1024 * 1024, PersistedSnapshotTier.Persisted))
        using (PersistedSnapshotRepository repo = new(smallArena2, smallBlobs2, catalogDb, new FlatDbConfig(), new PersistedSnapshotBloomFilterManager(), LimboLogs.Instance))
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
        using BlobArenaManager smallBlobs = new(Path.Combine(_testDir, "blobs", "small"), 1024 * 1024, PersistedSnapshotTier.Persisted);
        using PersistedSnapshotRepository repo = new(smallArena, smallBlobs, new MemDb(), new FlatDbConfig(), new PersistedSnapshotBloomFilterManager(), LimboLogs.Instance);
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

        repo.ConvertSnapshotToPersistedSnapshot(snap).Dispose();

        Assert.That(repo.TryLeaseSnapshotTo(s1, out PersistedSnapshot? persisted), Is.True);
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
        using ArenaManager smallArena = new(Path.Combine(_testDir, "arenas", "base"), 0, maxArenaSize: 4096);
        using BlobArenaManager smallBlobs = new(Path.Combine(_testDir, "blobs", "small"), 1024 * 1024, PersistedSnapshotTier.Persisted);
        using PersistedSnapshotRepository repo = new(smallArena, smallBlobs, new MemDb(), new FlatDbConfig(), new PersistedSnapshotBloomFilterManager(), LimboLogs.Instance);
        repo.LoadFromCatalog();

        StateId s0 = new(0, Keccak.EmptyTreeHash);
        StateId s1 = new(1, Keccak.Compute("1"));
        StateId s2 = new(2, Keccak.Compute("2"));
        StateId s3 = new(3, Keccak.Compute("3"));

        Snapshot snap1 = CreateTestSnapshot(s0, s1, TestItem.AddressA);
        Snapshot snap2 = CreateTestSnapshot(s1, s2, TestItem.AddressB);
        Snapshot snap3 = CreateTestSnapshot(s2, s3, TestItem.AddressC);

        repo.ConvertSnapshotToPersistedSnapshot(snap1).Dispose();
        repo.ConvertSnapshotToPersistedSnapshot(snap2).Dispose();
        repo.ConvertSnapshotToPersistedSnapshot(snap3).Dispose();
        Assert.That(repo.SnapshotCount, Is.EqualTo(3));

        // Remove states until block 2 (removes snap1 with To=1)
        repo.RemoveStatesUntil(2);
        Assert.That(repo.SnapshotCount, Is.EqualTo(2));
    }

    [TestCase(1)]
    [TestCase(2)]
    [TestCase(5)]
    public void TryGetSnapshotFrom_WalksBaseChainFromSeed(int chainLength)
    {
        using ArenaManager smallArena = new(Path.Combine(_testDir, "arenas", "base"), 0, maxArenaSize: 4096);
        using BlobArenaManager smallBlobs = new(Path.Combine(_testDir, "blobs", "small"), 1024 * 1024, PersistedSnapshotTier.Persisted);
        using PersistedSnapshotRepository repo = new(smallArena, smallBlobs, new MemDb(), new FlatDbConfig(), new PersistedSnapshotBloomFilterManager(), LimboLogs.Instance);
        repo.LoadFromCatalog();

        StateId[] states = new StateId[chainLength + 1];
        states[0] = new StateId(0, Keccak.EmptyTreeHash);
        for (int i = 1; i <= chainLength; i++)
        {
            states[i] = new StateId(i, Keccak.Compute($"s{i}"));
            repo.ConvertSnapshotToPersistedSnapshot(
                CreateTestSnapshot(states[i - 1], states[i], TestItem.Addresses[(i - 1) % TestItem.Addresses.Length])).Dispose();
        }

        // seed = top of chain; fromState = bottom. BFS must walk down via base.From edges
        // and return the base whose From matches states[0].
        PersistedSnapshot? hit = repo.TryGetSnapshotFrom(states[0], states[chainLength]);
        Assert.That(hit, Is.Not.Null);
        Assert.That(hit!.From, Is.EqualTo(states[0]));
        Assert.That(hit.To, Is.EqualTo(states[1]));
        hit.Dispose();
    }

    [Test]
    public void LastRegisteredState_TracksRegistrationsAcrossConvertAndPrune()
    {
        using ArenaManager smallArena = new(Path.Combine(_testDir, "arenas", "base"), 0, maxArenaSize: 4096);
        using BlobArenaManager smallBlobs = new(Path.Combine(_testDir, "blobs", "small"), 1024 * 1024, PersistedSnapshotTier.Persisted);
        using PersistedSnapshotRepository repo = new(smallArena, smallBlobs, new MemDb(), new FlatDbConfig(), new PersistedSnapshotBloomFilterManager(), LimboLogs.Instance);
        repo.LoadFromCatalog();

        Assert.That(repo.LastRegisteredState, Is.Null);

        StateId s0 = new(0, Keccak.EmptyTreeHash);
        StateId s1 = new(1, Keccak.Compute("1"));
        StateId s2 = new(2, Keccak.Compute("2"));
        repo.ConvertSnapshotToPersistedSnapshot(CreateTestSnapshot(s0, s1, TestItem.AddressA));
        Assert.That(repo.LastRegisteredState, Is.EqualTo(s1));

        repo.ConvertSnapshotToPersistedSnapshot(CreateTestSnapshot(s1, s2, TestItem.AddressB));
        Assert.That(repo.LastRegisteredState, Is.EqualTo(s2));

        // Pruning the tip rolls back to the next-highest remaining (s1).
        repo.RemoveStatesUntil(s2.BlockNumber);
        Assert.That(repo.SnapshotCount, Is.EqualTo(1));
        Assert.That(repo.LastRegisteredState, Is.EqualTo(s2),
            "RemoveStatesUntil(2) only removes entries with To.BlockNumber < 2, so s2 itself survives");

        repo.RemoveStatesUntil(99);
        Assert.That(repo.SnapshotCount, Is.EqualTo(0));
        Assert.That(repo.LastRegisteredState, Is.Null);
    }

    [Test]
    public void TryGetSnapshotFrom_Parameterless_SelfSeedsFromLastRegisteredState()
    {
        using ArenaManager smallArena = new(Path.Combine(_testDir, "arenas", "base"), 0, maxArenaSize: 4096);
        using BlobArenaManager smallBlobs = new(Path.Combine(_testDir, "blobs", "small"), 1024 * 1024, PersistedSnapshotTier.Persisted);
        using PersistedSnapshotRepository repo = new(smallArena, smallBlobs, new MemDb(), new FlatDbConfig(), new PersistedSnapshotBloomFilterManager(), LimboLogs.Instance);
        repo.LoadFromCatalog();

        // Empty repo: nothing to seed from.
        Assert.That(repo.TryGetSnapshotFrom(new StateId(0, Keccak.EmptyTreeHash)), Is.Null);

        const int chainLength = 4;
        StateId[] states = new StateId[chainLength + 1];
        states[0] = new StateId(0, Keccak.EmptyTreeHash);
        for (int i = 1; i <= chainLength; i++)
        {
            states[i] = new StateId(i, Keccak.Compute($"s{i}"));
            repo.ConvertSnapshotToPersistedSnapshot(
                CreateTestSnapshot(states[i - 1], states[i], TestItem.Addresses[(i - 1) % TestItem.Addresses.Length]));
        }

        // Parameterless overload must produce the same hit the seeded form does
        // when the explicit seed is exactly LastRegisteredState (= the chain's tip).
        PersistedSnapshot? selfSeed = repo.TryGetSnapshotFrom(states[0]);
        PersistedSnapshot? explicitSeed = repo.TryGetSnapshotFrom(states[0], states[chainLength]);

        Assert.That(selfSeed, Is.Not.Null);
        Assert.That(explicitSeed, Is.Not.Null);
        Assert.That(selfSeed!.From, Is.EqualTo(states[0]));
        Assert.That(selfSeed.To, Is.EqualTo(explicitSeed!.To));

        selfSeed.Dispose();
        explicitSeed.Dispose();
    }

    [Test]
    public void TryGetSnapshotFrom_EmptyRepo_ReturnsNull()
    {
        using ArenaManager smallArena = new(Path.Combine(_testDir, "arenas", "base"), 0, maxArenaSize: 4096);
        using BlobArenaManager smallBlobs = new(Path.Combine(_testDir, "blobs", "small"), 1024 * 1024, PersistedSnapshotTier.Persisted);
        using PersistedSnapshotRepository repo = new(smallArena, smallBlobs, new MemDb(), new FlatDbConfig(), new PersistedSnapshotBloomFilterManager(), LimboLogs.Instance);
        repo.LoadFromCatalog();

        StateId from = new(0, Keccak.EmptyTreeHash);
        StateId seed = new(5, Keccak.Compute("seed"));

        Assert.That(repo.TryGetSnapshotFrom(from, seed), Is.Null);
    }

    [TestCase(0)] // seed == fromState block
    [TestCase(-1)] // seed below fromState block (constructed via from at block 5)
    public void TryGetSnapshotFrom_SeedNotAboveTarget_ReturnsNull(int seedOffset)
    {
        using ArenaManager smallArena = new(Path.Combine(_testDir, "arenas", "base"), 0, maxArenaSize: 4096);
        using BlobArenaManager smallBlobs = new(Path.Combine(_testDir, "blobs", "small"), 1024 * 1024, PersistedSnapshotTier.Persisted);
        using PersistedSnapshotRepository repo = new(smallArena, smallBlobs, new MemDb(), new FlatDbConfig(), new PersistedSnapshotBloomFilterManager(), LimboLogs.Instance);
        repo.LoadFromCatalog();

        // Plant a real base whose From matches `from` so we'd otherwise have a hit.
        StateId from = new(5, Keccak.Compute("from"));
        StateId to = new(6, Keccak.Compute("to"));
        repo.ConvertSnapshotToPersistedSnapshot(CreateTestSnapshot(from, to, TestItem.AddressA)).Dispose();

        StateId seed = new(5 + seedOffset, Keccak.Compute("seed"));
        Assert.That(repo.TryGetSnapshotFrom(from, seed), Is.Null,
            "BFS must short-circuit when the seed isn't strictly above the target block");
    }

    [Test]
    public void TryGetSnapshotFrom_CompactedFromMatch_NotReturnedWhenBaseRemoved()
    {
        // Compacted [s0 → s8] exists and its From matches the target. Base[s1] (the lone
        // base whose From == s0) is pruned. BFS must navigate through the compacted skip
        // pointer for free but NEVER return the compacted entry — base-only is the new
        // contract — so the result is null.
        using ArenaManager arena = new(Path.Combine(_testDir, "arenas", "base"), 0, maxArenaSize: 256 * 1024);
        using BlobArenaManager blobs = new(Path.Combine(_testDir, "blobs", "small"), 4 * 1024 * 1024, PersistedSnapshotTier.Persisted);
        PersistedSnapshotBloomFilterManager blooms = new();
        using PersistedSnapshotRepository repo = new(arena, blobs, new MemDb(), new FlatDbConfig(), blooms, LimboLogs.Instance);
        repo.LoadFromCatalog();

        const int n = 8;
        IFlatDbConfig config = new FlatDbConfig { CompactSize = 4, MinCompactSize = 2 };
        PersistedSnapshotCompactor compactor = new(
            repo, arena, config, ScheduleHelper.CreateWithOffset(config, 0),
            Nethermind.Logging.LimboLogs.Instance, blooms,
            minCompactSize: config.CompactSize * 2,
            maxCompactSize: config.PersistedSnapshotMaxCompactSize);

        StateId[] states = new StateId[n + 1];
        states[0] = new StateId(0, Keccak.EmptyTreeHash);
        for (int i = 1; i <= n; i++)
        {
            states[i] = new StateId(i, Keccak.Compute($"s{i}"));
            repo.ConvertSnapshotToPersistedSnapshot(
                CreateTestSnapshot(states[i - 1], states[i], TestItem.Addresses[(i - 1) % TestItem.Addresses.Length])).Dispose();
        }

        compactor.DoCompactSnapshot(states[n]);
        Assert.That(repo.TryLeaseCompactedSnapshotTo(states[n], out PersistedSnapshot? compacted), Is.True);
        Assert.That(compacted!.From, Is.EqualTo(states[0]),
            "Test setup: compacted must cover s0..s8 so its From == target fromState");
        compacted.Dispose();

        // Sanity: with base[s1] still present, BFS finds it.
        PersistedSnapshot? withBase = repo.TryGetSnapshotFrom(states[0], states[n]);
        Assert.That(withBase, Is.Not.Null);
        Assert.That(withBase!.From, Is.EqualTo(states[0]));
        withBase.Dispose();

        // Remove base[s1] (To.BlockNumber < 2). Compacted survives (To=s8). Now no base has From==s0.
        repo.RemoveStatesUntil(2);
        Assert.That(repo.TryGetSnapshotFrom(states[0], states[n]), Is.Null,
            "Only the compacted entry has From==s0; base-only contract means we return null");
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
        using BlobArenaManager smallBlobs = new(Path.Combine(_testDir, "blobs", "small"), 1024 * 1024, PersistedSnapshotTier.Persisted);
        using PersistedSnapshotRepository repo = new(smallArena, smallBlobs, new MemDb(), new FlatDbConfig(), new PersistedSnapshotBloomFilterManager(), LimboLogs.Instance);
        repo.LoadFromCatalog();

        StateId prev = new(0, Keccak.EmptyTreeHash);
        for (int i = 1; i <= count; i++)
        {
            StateId next = new(i, Keccak.Compute($"s{i}"));
            Snapshot snap = CreateTestSnapshot(prev, next, TestItem.Addresses[i % TestItem.Addresses.Length]);
            repo.ConvertSnapshotToPersistedSnapshot(snap).Dispose();
            prev = next;
        }

        Assert.That(repo.SnapshotCount, Is.EqualTo(count));
        // Files stay packed: bounded by max file size / typical write size, not by snapshot count.
        int blobFileCount = Directory.GetFiles(Path.Combine(_testDir, "blobs", "small"), "blob_*.bin").Length;
        Assert.That(blobFileCount, Is.LessThan(count),
            "expected many base snapshots to share blob arena files");
    }

    [TestCase(true, TestName = "ConvertSnapshot_RecordsBlobRange(with trie nodes)")]
    [TestCase(false, TestName = "ConvertSnapshot_RecordsBlobRange(no trie nodes)")]
    public void ConvertSnapshot_RecordsBlobRange(bool withTrieNode)
    {
        using ArenaManager arena = new(Path.Combine(_testDir, "arenas", "base"), 0, maxArenaSize: 64 * 1024);
        using BlobArenaManager blobs = new(Path.Combine(_testDir, "blobs", "base"), 1024 * 1024, PersistedSnapshotTier.Persisted);
        using PersistedSnapshotRepository repo = new(arena, blobs, new MemDb(), new FlatDbConfig(), new PersistedSnapshotBloomFilterManager(), LimboLogs.Instance);
        repo.LoadFromCatalog();

        StateId s0 = new(0, Keccak.EmptyTreeHash);
        StateId s1 = new(1, Keccak.Compute("1"));
        SnapshotContent content = new();
        content.Accounts[TestItem.AddressA] = Build.An.Account.WithBalance(1000).TestObject;
        if (withTrieNode)
            content.StateNodes[new TreePath(Keccak.Compute("p"), 4)] = new TrieNode(NodeType.Leaf, [0xC2, 0x80, 0x80]);

        using PersistedSnapshot persisted = repo.ConvertSnapshotToPersistedSnapshot(
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

    [Test]
    public void LeaseBaseSnapshotsInRange_ReturnsBasesTilingWindow()
    {
        using ArenaManager arena = new(Path.Combine(_testDir, "arenas", "base"), 0, maxArenaSize: 64 * 1024);
        using BlobArenaManager blobs = new(Path.Combine(_testDir, "blobs", "base"), 1024 * 1024, PersistedSnapshotTier.Persisted);
        using PersistedSnapshotRepository repo = new(arena, blobs, new MemDb(), new FlatDbConfig(), new PersistedSnapshotBloomFilterManager(), LimboLogs.Instance);
        repo.LoadFromCatalog();

        StateId[] ids = new StateId[4];
        ids[0] = new(0, Keccak.EmptyTreeHash);
        for (int i = 1; i < 4; i++)
        {
            ids[i] = new(i, Keccak.Compute($"s{i}"));
            repo.ConvertSnapshotToPersistedSnapshot(
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
    /// the bloom manager's slots must be filled from the WIDEST snapshot covering each
    /// state (a compacted/persistable bloom wins over a per-base bloom in its range),
    /// and every slot inside a compacted snapshot's range must resolve to the SAME bloom
    /// instance via LeaseOrSentinel. Mirrors the manager end-state runtime would produce
    /// after a long-running session's compactions, without building one bloom per loaded
    /// snapshot the way the pre-fix LoadFromCatalog did.
    /// </summary>
    [Test]
    public void LoadFromCatalog_ReconstructsBloom_FromWidestCoveringSnapshot()
    {
        StateId[] ids = new StateId[5];
        ids[0] = new(0, Keccak.EmptyTreeHash);
        for (int i = 1; i <= 4; i++) ids[i] = new(i, Keccak.Compute($"s{i}"));

        MemDb catalogDb = new();
        string arenaDir = Path.Combine(_testDir, "arenas", "base");
        string blobDir = Path.Combine(_testDir, "blobs", "base");

        // Session 1: 4 bases + a CompactSize=4 persistable covering all 4 of them.
        using (ArenaManager arena1 = new(arenaDir, 0, maxArenaSize: 64 * 1024))
        using (BlobArenaManager blobs1 = new(blobDir, 1024 * 1024, PersistedSnapshotTier.Persisted))
        using (PersistedSnapshotBloomFilterManager bloomMgr1 = new())
        using (PersistedSnapshotRepository repo = new(arena1, blobs1, catalogDb, new FlatDbConfig(), bloomMgr1, LimboLogs.Instance))
        {
            repo.LoadFromCatalog();
            for (int i = 1; i <= 4; i++)
                repo.ConvertSnapshotToPersistedSnapshot(
                    CreateTestSnapshot(ids[i - 1], ids[i], TestItem.Addresses[i - 1])).Dispose();

            IFlatDbConfig config = new FlatDbConfig { CompactSize = 4, MinCompactSize = 2 };
            PersistedSnapshotCompactor compactor = new(
                repo, arena1, config,
                ScheduleHelper.CreateWithOffset(config, 0),
                Nethermind.Logging.LimboLogs.Instance, bloomMgr1,
                minCompactSize: 2, maxCompactSize: config.PersistedSnapshotMaxCompactSize);
            compactor.DoCompactPersistable(ids[4]);  // persistable at To=4 covering (0, 4]
        }

        // Session 2: reload. LoadFromCatalog now auto-calls ReconstructBloom.
        using PersistedSnapshotBloomFilterManager bloomMgr2 = new();
        using ArenaManager arena2 = new(arenaDir, 0, maxArenaSize: 64 * 1024);
        using BlobArenaManager blobs2 = new(blobDir, 1024 * 1024, PersistedSnapshotTier.Persisted);
        using PersistedSnapshotRepository repo2 = new(arena2, blobs2, catalogDb, new FlatDbConfig(), bloomMgr2, LimboLogs.Instance);
        repo2.LoadFromCatalog();

        // With the v7 (To, depth)-keyed catalog the base at ids[4] survives alongside the
        // persistable at the same To — both buckets must lease independently.
        Assert.That(repo2.TryLeaseSnapshotTo(ids[4], out PersistedSnapshot? baseAt4), Is.True,
            "base at the persistable's To must round-trip under v7");
        baseAt4!.Dispose();
        Assert.That(repo2.TryLeasePersistableCompactedSnapshotTo(ids[4], out PersistedSnapshot? persistableAt4), Is.True);
        persistableAt4!.Dispose();

        // Every slot in (0, 4] must resolve to the SAME bloom instance — the persistable's
        // merged bloom, which the range walk in Register spread across the slot dict.
        using PersistedSnapshotBloom b1 = bloomMgr2.LeaseOrSentinel(ids[1]);
        using PersistedSnapshotBloom b2 = bloomMgr2.LeaseOrSentinel(ids[2]);
        using PersistedSnapshotBloom b3 = bloomMgr2.LeaseOrSentinel(ids[3]);
        using PersistedSnapshotBloom b4 = bloomMgr2.LeaseOrSentinel(ids[4]);

        Assert.That(b1, Is.Not.SameAs(PersistedSnapshotBloom.AlwaysTrue),
            "ReconstructBloom must have built a real bloom for every covered slot");
        Assert.That(b1, Is.SameAs(b2), "slots in compacted range share the same bloom instance");
        Assert.That(b2, Is.SameAs(b3));
        Assert.That(b3, Is.SameAs(b4));
        Assert.That(b1.From.BlockNumber, Is.EqualTo(0));
        Assert.That(b1.To.BlockNumber, Is.EqualTo(4));

        // Every address written across the 4 bases must be present in the merged bloom —
        // it was built from the persistable's HSST, not from any one base.
        for (int i = 1; i <= 4; i++)
        {
            ulong key = PersistedSnapshotBloomBuilder.AddressKey(TestItem.Addresses[i - 1]);
            Assert.That(b1.Bloom.MightContain(key), Is.True,
                $"AddressKey for base {i} must be in the persistable's merged bloom");
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
        string arenaDir = Path.Combine(_testDir, "arenas", "rt");
        string blobDir = Path.Combine(_testDir, "blobs", "rt");

        using (ArenaManager arena1 = new(arenaDir, 0, maxArenaSize: 64 * 1024))
        using (BlobArenaManager blobs1 = new(blobDir, 1024 * 1024, PersistedSnapshotTier.Persisted))
        using (PersistedSnapshotBloomFilterManager bloomMgr1 = new())
        using (PersistedSnapshotRepository repo = new(arena1, blobs1, catalogDb, new FlatDbConfig(), bloomMgr1, LimboLogs.Instance))
        {
            repo.LoadFromCatalog();
            for (int i = 1; i <= 4; i++)
                repo.ConvertSnapshotToPersistedSnapshot(
                    CreateTestSnapshot(ids[i - 1], ids[i], TestItem.Addresses[i - 1])).Dispose();

            IFlatDbConfig config = new FlatDbConfig { CompactSize = 4, MinCompactSize = 2 };
            PersistedSnapshotCompactor compactor = new(
                repo, arena1, config,
                ScheduleHelper.CreateWithOffset(config, 0),
                Nethermind.Logging.LimboLogs.Instance, bloomMgr1,
                minCompactSize: 2, maxCompactSize: config.PersistedSnapshotMaxCompactSize);
            compactor.DoCompactPersistable(ids[4]);

            Assert.That(repo.SnapshotCount, Is.EqualTo(5), "session 1 must hold 4 bases + 1 persistable");
        }

        using PersistedSnapshotBloomFilterManager bloomMgr2 = new();
        using ArenaManager arena2 = new(arenaDir, 0, maxArenaSize: 64 * 1024);
        using BlobArenaManager blobs2 = new(blobDir, 1024 * 1024, PersistedSnapshotTier.Persisted);
        using PersistedSnapshotRepository repo2 = new(arena2, blobs2, catalogDb, new FlatDbConfig(), bloomMgr2, LimboLogs.Instance);
        repo2.LoadFromCatalog();

        Assert.That(repo2.SnapshotCount, Is.EqualTo(5),
            "all five snapshots (4 bases + 1 persistable at the last base's To) must round-trip under v7");
        for (int i = 1; i <= 4; i++)
        {
            Assert.That(repo2.TryLeaseSnapshotTo(ids[i], out PersistedSnapshot? b), Is.True,
                $"base at ids[{i}] must survive reload");
            b!.Dispose();
        }
        Assert.That(repo2.TryLeasePersistableCompactedSnapshotTo(ids[4], out PersistedSnapshot? persistable), Is.True);
        persistable!.Dispose();
    }

    /// <summary>
    /// Exercise the parallel-then-serial split in <c>LoadFromCatalog</c>: build enough
    /// snapshots in session 1 to spread across multiple <see cref="System.Threading.Tasks.Parallel.ForEach"/>
    /// partitions, reload in session 2, and verify the parallel construction + serial
    /// sorted-set rebuild preserves: snapshot count, per-bucket leasability, ordered-id
    /// invariants (the From/To chain reachable via <c>TryGetSnapshotFrom</c>), and the
    /// ReconstructBloom end-state (every slot in a compacted range resolves to the same
    /// bloom). Stays below <c>ParallelLoadThreshold</c> so the progress logger is bypassed —
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
        string arenaDir = Path.Combine(_testDir, "arenas", "par");
        string blobDir = Path.Combine(_testDir, "blobs", "par");

        using (ArenaManager arena1 = new(arenaDir, 0, maxArenaSize: 64 * 1024))
        using (BlobArenaManager blobs1 = new(blobDir, 1024 * 1024, PersistedSnapshotTier.Persisted))
        using (PersistedSnapshotBloomFilterManager bloomMgr1 = new())
        using (PersistedSnapshotRepository repo = new(arena1, blobs1, catalogDb, new FlatDbConfig(), bloomMgr1, LimboLogs.Instance))
        {
            repo.LoadFromCatalog();
            for (int i = 1; i <= N; i++)
                repo.ConvertSnapshotToPersistedSnapshot(
                    CreateTestSnapshot(ids[i - 1], ids[i], TestItem.Addresses[(i - 1) % TestItem.Addresses.Length])).Dispose();

            // Throw in two persistables (CompactSize=8) at boundaries 8 and 16 so the
            // catalog has multi-bucket entries that exercise the bucket-routing branch
            // in the parallel LoadSnapshot.
            IFlatDbConfig config = new FlatDbConfig { CompactSize = 8, MinCompactSize = 2 };
            PersistedSnapshotCompactor compactor = new(
                repo, arena1, config,
                ScheduleHelper.CreateWithOffset(config, 0),
                Nethermind.Logging.LimboLogs.Instance, bloomMgr1,
                minCompactSize: 2, maxCompactSize: config.PersistedSnapshotMaxCompactSize);
            compactor.DoCompactPersistable(ids[8]);
            compactor.DoCompactPersistable(ids[16]);
        }

        using PersistedSnapshotBloomFilterManager bloomMgr2 = new();
        using ArenaManager arena2 = new(arenaDir, 0, maxArenaSize: 64 * 1024);
        using BlobArenaManager blobs2 = new(blobDir, 1024 * 1024, PersistedSnapshotTier.Persisted);
        using PersistedSnapshotRepository repo2 = new(arena2, blobs2, catalogDb, new FlatDbConfig(), bloomMgr2, LimboLogs.Instance);
        repo2.LoadFromCatalog();

        // All N bases + 2 persistables survive.
        Assert.That(repo2.SnapshotCount, Is.EqualTo(N + 2));
        for (int i = 1; i <= N; i++)
        {
            Assert.That(repo2.TryLeaseSnapshotTo(ids[i], out PersistedSnapshot? b), Is.True, $"base ids[{i}] missing");
            b!.Dispose();
        }
        Assert.That(repo2.TryLeasePersistableCompactedSnapshotTo(ids[8], out PersistedSnapshot? p8), Is.True);
        p8!.Dispose();
        Assert.That(repo2.TryLeasePersistableCompactedSnapshotTo(ids[16], out PersistedSnapshot? p16), Is.True);
        p16!.Dispose();

        // Ordered-id invariant: a backward walk from the newest base via the From chain
        // visits every block down to genesis. Catches a missing or mis-routed sorted-set entry.
        for (int i = N; i >= 1; i--)
        {
            PersistedSnapshot? hop = repo2.TryGetSnapshotFrom(ids[i - 1]);
            Assert.That(hop, Is.Not.Null, $"no snapshot found from ids[{i - 1}]");
            hop!.Dispose();
        }

        // Bloom end-state: every slot in (0, 8] resolves to the SAME bloom (the persistable
        // at ids[8]'s merged bloom propagated by Register's chain walk).
        using PersistedSnapshotBloom bloomAt1 = bloomMgr2.LeaseOrSentinel(ids[1]);
        using PersistedSnapshotBloom bloomAt8 = bloomMgr2.LeaseOrSentinel(ids[8]);
        Assert.That(bloomAt1, Is.Not.SameAs(PersistedSnapshotBloom.AlwaysTrue));
        Assert.That(bloomAt1, Is.SameAs(bloomAt8), "slots covered by the same persistable share a bloom");
    }
}
