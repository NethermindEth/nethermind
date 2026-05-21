// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Int256;
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
        using BlobArenaManager smallBlobs = new(Path.Combine(_testDir, "blobs", "small"), 1024 * 1024, PersistedSnapshotTier.Small);
        using PersistedSnapshotRepository repo = new(smallArena, smallBlobs, new MemDb(), new FlatDbConfig(), new PersistedSnapshotBloomFilterManager());
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
        using BlobArenaManager smallBlobs = new(Path.Combine(_testDir, "blobs", "small"), 4 * 1024 * 1024, PersistedSnapshotTier.Small);
        using PersistedSnapshotRepository repo = new(smallArena, smallBlobs, new MemDb(), new FlatDbConfig(), new PersistedSnapshotBloomFilterManager());
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
        using BlobArenaManager smallBlobs = new(Path.Combine(_testDir, "blobs", "small"), 1024 * 1024, PersistedSnapshotTier.Small);
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
        using (BlobArenaManager smallBlobs1 = new(Path.Combine(_testDir, "blobs", "small"), 1024 * 1024, PersistedSnapshotTier.Small))
        using (PersistedSnapshotRepository repo = new(smallArena1, smallBlobs1, catalogDb, new FlatDbConfig(), new PersistedSnapshotBloomFilterManager()))
        {
            repo.LoadFromCatalog();
            Snapshot snap = CreateTestSnapshot(s0, s1, TestItem.AddressA);
            repo.ConvertSnapshotToPersistedSnapshot(snap).Dispose();
        }

        // Session 2: reload from disk
        using (ArenaManager smallArena2 = new(Path.Combine(_testDir, "arenas", "base"), 0, maxArenaSize: 4096))
        using (BlobArenaManager smallBlobs2 = new(Path.Combine(_testDir, "blobs", "small"), 1024 * 1024, PersistedSnapshotTier.Small))
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
        using BlobArenaManager smallBlobs = new(Path.Combine(_testDir, "blobs", "small"), 1024 * 1024, PersistedSnapshotTier.Small);
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
    public void PruneBefore_RemovesOldSnapshots()
    {
        using ArenaManager smallArena = new(Path.Combine(_testDir, "arenas", "base"), 0, maxArenaSize: 4096);
        using BlobArenaManager smallBlobs = new(Path.Combine(_testDir, "blobs", "small"), 1024 * 1024, PersistedSnapshotTier.Small);
        using PersistedSnapshotRepository repo = new(smallArena, smallBlobs, new MemDb(), new FlatDbConfig(), new PersistedSnapshotBloomFilterManager());
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

        // Prune before block 2 (removes snap1 with To=1)
        int pruned = repo.PruneBefore(new StateId(2, Keccak.Compute("prune")));
        Assert.That(pruned, Is.EqualTo(1));
        Assert.That(repo.SnapshotCount, Is.EqualTo(2));
    }

    [TestCase(1)]
    [TestCase(2)]
    [TestCase(5)]
    public void TryGetSnapshotFrom_WalksBaseChainFromSeed(int chainLength)
    {
        using ArenaManager smallArena = new(Path.Combine(_testDir, "arenas", "base"), 0, maxArenaSize: 4096);
        using BlobArenaManager smallBlobs = new(Path.Combine(_testDir, "blobs", "small"), 1024 * 1024, PersistedSnapshotTier.Small);
        using PersistedSnapshotRepository repo = new(smallArena, smallBlobs, new MemDb(), new FlatDbConfig(), new PersistedSnapshotBloomFilterManager());
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
        using BlobArenaManager smallBlobs = new(Path.Combine(_testDir, "blobs", "small"), 1024 * 1024, PersistedSnapshotTier.Small);
        using PersistedSnapshotRepository repo = new(smallArena, smallBlobs, new MemDb(), new FlatDbConfig(), new PersistedSnapshotBloomFilterManager());
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
        int pruned = repo.PruneBefore(s2);
        Assert.That(pruned, Is.EqualTo(1));
        Assert.That(repo.LastRegisteredState, Is.EqualTo(s2),
            "PruneBefore(s2) only removes entries with To.BlockNumber < 2, so s2 itself survives");

        pruned = repo.PruneBefore(new StateId(99, Keccak.EmptyTreeHash));
        Assert.That(pruned, Is.EqualTo(1));
        Assert.That(repo.LastRegisteredState, Is.Null);
    }

    [Test]
    public void TryGetSnapshotFrom_Parameterless_SelfSeedsFromLastRegisteredState()
    {
        using ArenaManager smallArena = new(Path.Combine(_testDir, "arenas", "base"), 0, maxArenaSize: 4096);
        using BlobArenaManager smallBlobs = new(Path.Combine(_testDir, "blobs", "small"), 1024 * 1024, PersistedSnapshotTier.Small);
        using PersistedSnapshotRepository repo = new(smallArena, smallBlobs, new MemDb(), new FlatDbConfig(), new PersistedSnapshotBloomFilterManager());
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
        using BlobArenaManager smallBlobs = new(Path.Combine(_testDir, "blobs", "small"), 1024 * 1024, PersistedSnapshotTier.Small);
        using PersistedSnapshotRepository repo = new(smallArena, smallBlobs, new MemDb(), new FlatDbConfig(), new PersistedSnapshotBloomFilterManager());
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
        using BlobArenaManager smallBlobs = new(Path.Combine(_testDir, "blobs", "small"), 1024 * 1024, PersistedSnapshotTier.Small);
        using PersistedSnapshotRepository repo = new(smallArena, smallBlobs, new MemDb(), new FlatDbConfig(), new PersistedSnapshotBloomFilterManager());
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
        using BlobArenaManager blobs = new(Path.Combine(_testDir, "blobs", "small"), 4 * 1024 * 1024, PersistedSnapshotTier.Small);
        PersistedSnapshotBloomFilterManager blooms = new();
        using PersistedSnapshotRepository repo = new(arena, blobs, new MemDb(), new FlatDbConfig(), blooms);
        repo.LoadFromCatalog();

        const int n = 8;
        IFlatDbConfig config = new FlatDbConfig { CompactSize = 4, MinCompactSize = 2 };
        PersistedSnapshotCompactor compactor = new(
            repo, arena, config, Nethermind.Logging.LimboLogs.Instance, blooms,
            minCompactSize: config.CompactSize * 2,
            maxCompactSize: config.PersistedSnapshotMaxCompactSize,
            tier: PersistedSnapshotTier.Large);

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

        // Prune base[s1] (To.BlockNumber < 2). Compacted survives (To=s8). Now no base has From==s0.
        repo.PruneBefore(new StateId(2, Keccak.Compute("prune")));
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
        using BlobArenaManager smallBlobs = new(Path.Combine(_testDir, "blobs", "small"), 1024 * 1024, PersistedSnapshotTier.Small);
        using PersistedSnapshotRepository repo = new(smallArena, smallBlobs, new MemDb(), new FlatDbConfig(), new PersistedSnapshotBloomFilterManager());
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
}
