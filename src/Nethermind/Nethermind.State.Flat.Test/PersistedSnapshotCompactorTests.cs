// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Threading.Tasks;
using Nethermind.Logging;
using System.Collections.Generic;
using System.IO;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.Db;
using Nethermind.State.Flat.Hsst;
using Nethermind.State.Flat.Hsst.BTree;
using Nethermind.State.Flat.PersistedSnapshots;
using Nethermind.State.Flat.Persistence.BloomFilter;
using Nethermind.State.Flat.PersistedSnapshots.Storage;
using Nethermind.Trie;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test;

[TestFixture]
public class PersistedSnapshotCompactorTests
{
    private ResourcePool _pool = null!;
    private MemoryArenaManager _memArena = null!;

    [SetUp]
    public void SetUp()
    {
        _pool = new ResourcePool(new FlatDbConfig());
        _memArena = new MemoryArenaManager();
    }

    [TearDown]
    public void TearDown() =>
        _memArena.Dispose();

    /// <summary>
    /// Regression for large-tier compactions where N approaches the typical
    /// <c>compactSize/CompactSize</c> ceiling (~32). Each source carries a unique account
    /// plus a shared overlapping account (AddressA) with a distinct slot per block, so the
    /// per-address sub-tag merge runs with <c>matchCount == N</c> on every iteration and
    /// the slot merge exercises the fused inline bloom path with N slot inputs. Failures
    /// here flag mis-cached keys, missed bound refresh after <c>MoveNext</c>, or
    /// destruct-barrier/slot-bound mismatches in <c>NWayMergePerAddressHsst</c>.
    /// </summary>
    [TestCase(8)]
    [TestCase(16)]
    [TestCase(32)]
    public void TryCompactPersistedSnapshots_MergesNBaseSnapshots(int n)
    {
        string testDir = Path.Combine(Path.GetTempPath(), $"nethermind_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(testDir);
        try
        {
            using ArenaManager smallArena = new(Path.Combine(testDir, "arenas", "base"), 0, maxArenaSize: 256 * 1024);
            using BlobArenaManager smallBlobs = new(Path.Combine(testDir, "blobs", "small"), 4 * 1024 * 1024, PersistedSnapshotTier.Persisted);
            using PersistedSnapshotRepository repo = new(smallArena, smallBlobs, new MemDb(), new FlatDbConfig(), new PersistedSnapshotBloomFilterManager(), LimboLogs.Instance);
            repo.LoadFromCatalog();

            // CompactSize=4 → minCompactSize for the large-tier compactor is 8. n is a power of 2
            // in {8, 16, 32}, so n & -n == n covers the whole window and triggers a single merge.
            IFlatDbConfig config = new FlatDbConfig { CompactSize = 4, MinCompactSize = 2 };
            PersistedSnapshotCompactor compactor = new(
                repo, smallArena, config,
                ScheduleHelper.CreateWithOffset(config, 0),
                Nethermind.Logging.LimboLogs.Instance, new PersistedSnapshotBloomFilterManager(),
                minCompactSize: config.CompactSize * 2,
                maxCompactSize: config.PersistedSnapshotMaxCompactSize);

            StateId prev = new(0, Keccak.EmptyTreeHash);
            for (int i = 1; i <= n; i++)
            {
                StateId next = new(i, Keccak.Compute($"s{i}"));
                SnapshotContent c = new();
                // Unique account per block (different address each time).
                c.Accounts[TestItem.Addresses[i - 1]] = Build.An.Account.WithBalance((UInt256)(i * 100)).TestObject;
                // Shared overlapping account: same AddressA every block, distinct balance and
                // a distinct slot — drives matchCount == N through NWayMergePerAddressHsst,
                // and the slot merge sees N inputs with N unique slot keys.
                c.Accounts[TestItem.AddressA] = Build.An.Account.WithBalance((UInt256)i).TestObject;
                c.Storages[(TestItem.AddressA, (UInt256)i)] = new SlotValue(new byte[] { (byte)i });
                repo.ConvertSnapshotToPersistedSnapshot(new Snapshot(prev, next, c, _pool, ResourcePool.Usage.MainBlockProcessing)).Dispose();
                prev = next;
            }

            compactor.DoCompactSnapshot(prev);

            Assert.That(repo.TryLeaseCompactedSnapshotTo(prev, out PersistedSnapshot? compacted), Is.True);
            try
            {
                Assert.That(compacted!.From.BlockNumber, Is.EqualTo(0));
                Assert.That(compacted.To.BlockNumber, Is.EqualTo(n));

                // Every unique account must survive.
                for (int i = 1; i <= n; i++)
                {
                    Assert.That(compacted.TryGetAccount(TestItem.Addresses[i - 1], out _), Is.True,
                        $"Account from block {i} missing");
                }

                // Overlapping account: newest balance wins.
                Assert.That(compacted.TryGetAccount(TestItem.AddressA, out Account? a), Is.True);
                Assert.That(a!.Balance, Is.EqualTo((UInt256)n), "Newest balance must win on the overlapping account");

                // Every per-block slot must survive (each block wrote a distinct slot index).
                for (int i = 1; i <= n; i++)
                {
                    SlotValue slot = default;
                    Assert.That(compacted.TryGetSlot(TestItem.AddressA, (UInt256)i, ref slot), Is.True,
                        $"Slot {i} must survive merge");
                    Assert.That(slot.AsReadOnlySpan.ToArray(), Is.EqualTo(new SlotValue(new byte[] { (byte)i }).AsReadOnlySpan.ToArray()),
                        $"Slot {i} value mismatch");
                }
            }
            finally { compacted!.Dispose(); }
        }
        finally
        {
            if (Directory.Exists(testDir))
                Directory.Delete(testDir, recursive: true);
        }
    }

    /// <summary>
    /// Regression for large-tier boundary compaction of an address with 256k sequential
    /// storage slots. Each big-endian-contiguous run of 65536 slots forms one dense 30-byte
    /// slot-prefix group; merging the per-block slices accumulates a group's inner sub-slot
    /// HSST past <c>ArenaBufferWriter</c>'s 1 MiB buffer. No single source snapshot crosses
    /// that threshold (16384 slots per block), so the oversized value first appears inside
    /// <c>NWayNestedStreamingSlotMerge</c> during the merge — the mainnet crash site.
    /// </summary>
    [Test]
    public void DoCompactSnapshot_SequentialSlotsAcrossDensePrefixGroups_RoundTrips()
    {
        const int snapshotCount = 16;
        const int slotsPerSnapshot = 16 * 1024; // 16 × 16384 = 256k merged slots

        string testDir = Path.Combine(Path.GetTempPath(), $"nethermind_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(testDir);
        try
        {
            // 64 MiB shared arena: the per-block snapshots and the ~10 MiB compacted output
            // stay below the 512 MiB dedicated-arena threshold, so each must fit a shared file.
            using ArenaManager smallArena = new(Path.Combine(testDir, "arenas", "base"), 0, maxArenaSize: 64 * 1024 * 1024);
            using BlobArenaManager smallBlobs = new(Path.Combine(testDir, "blobs", "small"), 4 * 1024 * 1024, PersistedSnapshotTier.Persisted);
            using PersistedSnapshotRepository repo = new(smallArena, smallBlobs, new MemDb(), new FlatDbConfig(), new PersistedSnapshotBloomFilterManager(), LimboLogs.Instance);
            repo.LoadFromCatalog();

            IFlatDbConfig config = new FlatDbConfig { CompactSize = 4, MinCompactSize = 2 };
            PersistedSnapshotCompactor compactor = new(
                repo, smallArena, config,
                ScheduleHelper.CreateWithOffset(config, 0),
                Nethermind.Logging.LimboLogs.Instance, new PersistedSnapshotBloomFilterManager(),
                minCompactSize: config.CompactSize * 2,
                maxCompactSize: config.PersistedSnapshotMaxCompactSize);

            // Each block writes a contiguous 16384-slot slice on AddressA. A slice stays well
            // under ArenaBufferWriter's 1 MiB buffer, so every per-block build succeeds; only
            // the merged 65536-slot prefix groups cross the threshold.
            StateId prev = new(0, Keccak.EmptyTreeHash);
            for (int i = 1; i <= snapshotCount; i++)
            {
                StateId next = new(i, Keccak.Compute($"s{i}"));
                SnapshotContent c = new();
                TestFixtureHelpers.AddSequentialSlots(c, TestItem.AddressA,
                    firstSlot: (i - 1) * slotsPerSnapshot + 1, count: slotsPerSnapshot);
                repo.ConvertSnapshotToPersistedSnapshot(
                    new Snapshot(prev, next, c, _pool, ResourcePool.Usage.MainBlockProcessing)).Dispose();
                prev = next;
            }

            compactor.DoCompactSnapshot(prev);

            Assert.That(repo.TryLeaseCompactedSnapshotTo(prev, out PersistedSnapshot? compacted), Is.True);
            try
            {
                int totalSlots = snapshotCount * slotsPerSnapshot;
                foreach (int probe in new[] { 1, 65535, 65536, 131072, totalSlots })
                {
                    SlotValue slot = default;
                    Assert.That(compacted!.TryGetSlot(TestItem.AddressA, (UInt256)probe, ref slot), Is.True, $"slot {probe} missing");
                    Assert.That(slot.AsReadOnlySpan.SequenceEqual(TestFixtureHelpers.SequentialSlotValue(probe)), Is.True,
                        $"slot {probe} value mismatch");
                }
            }
            finally { compacted!.Dispose(); }
        }
        finally
        {
            if (Directory.Exists(testDir))
                Directory.Delete(testDir, recursive: true);
        }
    }

    // A storage slot whose first-30-byte prefix is distinct per id (id in the low limb, above
    // the 2-byte suffix), so N ids yield N distinct slot-prefix groups — the input shape that
    // drives the slot-prefix HSST past the hashtable threshold into the 0x08 partitioned layout.
    private static UInt256 DistinctPrefixSlot(int id) => new((ulong)id << 16);
    private static byte[] DistinctPrefixSlotValue(int id) => [(byte)(id % 255 + 1)];

    // Distinct 20-byte address carrying the id big-endian in the last 4 bytes.
    private static Address DistinctAddress(int id)
    {
        byte[] b = new byte[20];
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(16), (uint)id);
        return new Address(b);
    }

    /// <summary>
    /// End-to-end of the partitioned slot HSST (IndexType 0x08) on the BASE build path: one
    /// snapshot with 200 distinct slot prefixes (~6 KiB of 30-byte keys, above the 4 KiB
    /// hashtable threshold) forces a hashtable-bearing partitioned blob, then every slot is
    /// read back through the real mmap reader stack via <c>TryGetSlot</c>.
    /// </summary>
    [Test]
    public void Base_ManyDistinctSlotPrefixes_RoundTrips_ThroughPartitionedHsst()
    {
        const int slotCount = 200;
        string testDir = Path.Combine(Path.GetTempPath(), $"nethermind_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(testDir);
        try
        {
            using ArenaManager smallArena = new(Path.Combine(testDir, "arenas", "base"), 0, maxArenaSize: 4 * 1024 * 1024);
            using BlobArenaManager smallBlobs = new(Path.Combine(testDir, "blobs", "small"), 4 * 1024 * 1024, PersistedSnapshotTier.Persisted);
            using PersistedSnapshotRepository repo = new(smallArena, smallBlobs, new MemDb(), new FlatDbConfig(), new PersistedSnapshotBloomFilterManager(), LimboLogs.Instance);
            repo.LoadFromCatalog();

            SnapshotContent c = new();
            c.Accounts[TestItem.AddressA] = Build.An.Account.WithBalance(1).TestObject;
            for (int id = 1; id <= slotCount; id++)
                c.Storages[(TestItem.AddressA, DistinctPrefixSlot(id))] = new SlotValue(DistinctPrefixSlotValue(id));

            PersistedSnapshot baseSnap = repo.ConvertSnapshotToPersistedSnapshot(
                new Snapshot(new StateId(0, Keccak.EmptyTreeHash), new StateId(1, Keccak.Compute("s1")), c, _pool, ResourcePool.Usage.MainBlockProcessing));
            try
            {
                for (int id = 1; id <= slotCount; id++)
                {
                    SlotValue slot = default;
                    Assert.That(baseSnap.TryGetSlot(TestItem.AddressA, DistinctPrefixSlot(id), ref slot), Is.True, $"slot {id} missing");
                    Assert.That(slot.AsReadOnlySpan.ToArray(), Is.EqualTo(new SlotValue(DistinctPrefixSlotValue(id)).AsReadOnlySpan.ToArray()), $"slot {id} value mismatch");
                }
                // A slot that was never written must be absent.
                SlotValue absent = default;
                Assert.That(baseSnap.TryGetSlot(TestItem.AddressA, DistinctPrefixSlot(slotCount + 1000), ref absent), Is.False);
            }
            finally { baseSnap.Dispose(); }
        }
        finally
        {
            if (Directory.Exists(testDir)) Directory.Delete(testDir, recursive: true);
        }
    }

    /// <summary>
    /// End-to-end of the partitioned slot HSST on the COMPACTION path: four base snapshots each
    /// write a disjoint 50-slot distinct-prefix range on AddressA (each base stays sub-threshold
    /// → 0x07), but the merged 200-prefix output crosses the hashtable threshold, so
    /// <c>NWayMergeKeyFirstPartitioned</c> emits a 0x08 blob. Every slot must survive and read
    /// back through the compacted snapshot's real reader.
    /// </summary>
    [Test]
    public void Compact_ManyDistinctSlotPrefixes_RoundTrips_ThroughPartitionedHsst()
    {
        // n is a power of 2 ≥ minCompactSize (CompactSize*2 = 8) so a single merge fires (mirrors
        // TryCompactPersistedSnapshots_MergesNBaseSnapshots). 8 × 25 = 200 distinct merged prefixes
        // → > 4 KiB of 30-byte keys → the merged slot HSST is the 0x08 partitioned layout.
        const int n = 8;
        const int slotsPerSnapshot = 25;
        string testDir = Path.Combine(Path.GetTempPath(), $"nethermind_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(testDir);
        try
        {
            using ArenaManager smallArena = new(Path.Combine(testDir, "arenas", "base"), 0, maxArenaSize: 4 * 1024 * 1024);
            using BlobArenaManager smallBlobs = new(Path.Combine(testDir, "blobs", "small"), 4 * 1024 * 1024, PersistedSnapshotTier.Persisted);
            using PersistedSnapshotRepository repo = new(smallArena, smallBlobs, new MemDb(), new FlatDbConfig(), new PersistedSnapshotBloomFilterManager(), LimboLogs.Instance);
            repo.LoadFromCatalog();

            IFlatDbConfig config = new FlatDbConfig { CompactSize = 4, MinCompactSize = 2 };
            PersistedSnapshotCompactor compactor = new(
                repo, smallArena, config,
                ScheduleHelper.CreateWithOffset(config, 0),
                LimboLogs.Instance, new PersistedSnapshotBloomFilterManager(),
                minCompactSize: config.CompactSize * 2,
                maxCompactSize: config.PersistedSnapshotMaxCompactSize);

            StateId prev = new(0, Keccak.EmptyTreeHash);
            for (int i = 1; i <= n; i++)
            {
                StateId next = new(i, Keccak.Compute($"s{i}"));
                SnapshotContent c = new();
                c.Accounts[TestItem.AddressA] = Build.An.Account.WithBalance((UInt256)i).TestObject;
                for (int k = 0; k < slotsPerSnapshot; k++)
                {
                    int id = (i - 1) * slotsPerSnapshot + k + 1;
                    c.Storages[(TestItem.AddressA, DistinctPrefixSlot(id))] = new SlotValue(DistinctPrefixSlotValue(id));
                }
                repo.ConvertSnapshotToPersistedSnapshot(new Snapshot(prev, next, c, _pool, ResourcePool.Usage.MainBlockProcessing)).Dispose();
                prev = next;
            }

            compactor.DoCompactSnapshot(prev);

            Assert.That(repo.TryLeaseCompactedSnapshotTo(prev, out PersistedSnapshot? compacted), Is.True);
            try
            {
                for (int id = 1; id <= n * slotsPerSnapshot; id++)
                {
                    SlotValue slot = default;
                    Assert.That(compacted!.TryGetSlot(TestItem.AddressA, DistinctPrefixSlot(id), ref slot), Is.True, $"slot {id} missing after compaction");
                    Assert.That(slot.AsReadOnlySpan.ToArray(), Is.EqualTo(new SlotValue(DistinctPrefixSlotValue(id)).AsReadOnlySpan.ToArray()), $"slot {id} value mismatch after compaction");
                }
            }
            finally { compacted!.Dispose(); }
        }
        finally
        {
            if (Directory.Exists(testDir)) Directory.Delete(testDir, recursive: true);
        }
    }

    /// <summary>
    /// Reproduces the "stale balance, same nonce" symptom shape: each address is updated in a
    /// DIFFERENT prefix of the 8 snapshots (balance changes, nonce fixed at 243), so the newest
    /// version lives in a different snapshot per address and matchCount ranges 1..8 across the
    /// merge. After compaction every account must carry its NEWEST balance — a newest-version-lost
    /// bug in the partitioned merge surfaces here as a stale balance.
    /// </summary>
    [Test]
    public void Compact_PerAddressNewestWins_AcrossVaryingMatchCount()
    {
        const int A = 64; // 0x0A address column
        const int S = 8;
        string testDir = Path.Combine(Path.GetTempPath(), $"nethermind_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(testDir);
        try
        {
            using ArenaManager smallArena = new(Path.Combine(testDir, "arenas", "base"), 0, maxArenaSize: 8 * 1024 * 1024);
            using BlobArenaManager smallBlobs = new(Path.Combine(testDir, "blobs", "small"), 8 * 1024 * 1024, PersistedSnapshotTier.Persisted);
            FlatDbConfig config = new()
            {
                CompactSize = 4,
                MinCompactSize = 2,
                PersistedSnapshotSlotPartitionThresholdBytes = 200,
                PersistedSnapshotSlotHashtableMinBytes = 0,
            };
            using PersistedSnapshotRepository repo = new(smallArena, smallBlobs, new MemDb(), config, new PersistedSnapshotBloomFilterManager(), LimboLogs.Instance);
            repo.LoadFromCatalog();
            PersistedSnapshotCompactor compactor = new(
                repo, smallArena, config, ScheduleHelper.CreateWithOffset(config, 0),
                LimboLogs.Instance, new PersistedSnapshotBloomFilterManager(),
                minCompactSize: config.CompactSize * 2, maxCompactSize: config.PersistedSnapshotMaxCompactSize);

            // newestSnap[a] = the last snapshot that updated address a (1..S).
            int[] newestSnap = new int[A];
            for (int a = 0; a < A; a++) newestSnap[a] = (a % S) + 1;

            static UInt256 Bal(int s, int a) => (UInt256)(((ulong)s << 40) | ((ulong)a << 8) | 0x6D);

            StateId prev = new(0, Keccak.EmptyTreeHash);
            for (int s = 1; s <= S; s++)
            {
                StateId next = new(s, Keccak.Compute($"s{s}"));
                SnapshotContent c = new();
                for (int a = 0; a < A; a++)
                {
                    if (s > newestSnap[a]) continue; // address a is only updated in snapshots 1..newestSnap[a]
                    // Same nonce in every update; only the balance changes (the symptom shape).
                    c.Accounts[DistinctAddress(a + 1)] = Build.An.Account.WithNonce(243).WithBalance(Bal(s, a)).TestObject;
                }
                repo.ConvertSnapshotToPersistedSnapshot(new Snapshot(prev, next, c, _pool, ResourcePool.Usage.MainBlockProcessing)).Dispose();
                prev = next;
            }

            compactor.DoCompactSnapshot(prev);
            Assert.That(repo.TryLeaseCompactedSnapshotTo(prev, out PersistedSnapshot? compacted), Is.True);
            try
            {
                for (int a = 0; a < A; a++)
                {
                    Address addr = DistinctAddress(a + 1);
                    UInt256 expected = Bal(newestSnap[a], a);
                    Assert.That(compacted!.TryGetAccount(addr, out Account? acc), Is.True, $"account {a} missing");
                    Assert.That(acc!.Nonce, Is.EqualTo((UInt256)243), $"account {a} nonce");
                    Assert.That(acc.Balance, Is.EqualTo(expected),
                        $"account {a} STALE balance: newest update was snapshot {newestSnap[a]} (matchCount={newestSnap[a]})");
                }
            }
            finally { compacted!.Dispose(); }
        }
        finally
        {
            if (Directory.Exists(testDir)) Directory.Delete(testDir, recursive: true);
        }
    }

    /// <summary>
    /// The compacted snapshot's bloom (built by the merge value-merger, NOT a rescan) must
    /// contain every address + slot key. In production the read bundle uses this bloom as a
    /// pre-filter, so a false negative makes the bundle SKIP the snapshot → stale read → a
    /// "random invalid block". Compacts a partitioned-column snapshot and asserts the
    /// merge-built bloom answers MightContain=true for every account and slot key.
    /// </summary>
    [Test]
    public void Compact_PartitionedColumns_MergeBuiltBloom_HasNoFalseNegatives()
    {
        const int n = 8; // shared 32 + unique 8×4 = 64 distinct addresses ⇒ 0x0A
        string testDir = Path.Combine(Path.GetTempPath(), $"nethermind_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(testDir);
        try
        {
            using ArenaManager smallArena = new(Path.Combine(testDir, "arenas", "base"), 0, maxArenaSize: 8 * 1024 * 1024);
            using BlobArenaManager smallBlobs = new(Path.Combine(testDir, "blobs", "small"), 8 * 1024 * 1024, PersistedSnapshotTier.Persisted);
            // Shared bloom manager so the compacted snapshot gets a real (non-empty) bloom.
            PersistedSnapshotBloomFilterManager bloomManager = new();
            FlatDbConfig config = new()
            {
                CompactSize = 4,
                MinCompactSize = 2,
                PersistedSnapshotSlotPartitionThresholdBytes = 200,
                PersistedSnapshotSlotHashtableMinBytes = 0,
            };
            using PersistedSnapshotRepository repo = new(smallArena, smallBlobs, new MemDb(), config, bloomManager, LimboLogs.Instance);
            repo.LoadFromCatalog();
            PersistedSnapshotCompactor compactor = new(
                repo, smallArena, config, ScheduleHelper.CreateWithOffset(config, 0),
                LimboLogs.Instance, bloomManager,
                minCompactSize: config.CompactSize * 2, maxCompactSize: config.PersistedSnapshotMaxCompactSize);

            // Addresses 1..32 are SHARED across every snapshot ⇒ matchCount == n ⇒ merged via
            // MergeValues (its bloom.Add path). Addresses >32 are unique to one snapshot ⇒
            // matchCount == 1 ⇒ merged via the fast-copy OnFastCopy path. Both must end up in the
            // merge-built bloom — the symptom account (repeatedly updated, high nonce) is a
            // MergeValues entry, which the previous disjoint-address version never exercised.
            const int shared = 32;
            const int uniquePerSnap = 4;
            HashSet<int> allAddrs = [];
            HashSet<(int a, ulong slotId)> allSlots = [];
            StateId prev = new(0, Keccak.EmptyTreeHash);
            for (int s = 1; s <= n; s++)
            {
                StateId next = new(s, Keccak.Compute($"s{s}"));
                SnapshotContent c = new();
                void Put(int a)
                {
                    allAddrs.Add(a);
                    Address addr = DistinctAddress(a);
                    c.Accounts[addr] = Build.An.Account.WithNonce(243).WithBalance((UInt256)(s * 1000 + a)).TestObject;
                    for (int i = 0; i < 3; i++)
                    {
                        ulong id = (ulong)(a * 100 + i);
                        c.Storages[(addr, DistinctPrefixSlot((int)id))] = new SlotValue([(byte)a, (byte)i, (byte)s]);
                        allSlots.Add((a, id));
                    }
                }
                for (int a = 1; a <= shared; a++) Put(a);                                  // shared ⇒ MergeValues
                for (int k = 0; k < uniquePerSnap; k++) Put(shared + (s - 1) * uniquePerSnap + k + 1); // unique ⇒ OnFastCopy
                repo.ConvertSnapshotToPersistedSnapshot(new Snapshot(prev, next, c, _pool, ResourcePool.Usage.MainBlockProcessing)).Dispose();
                prev = next;
            }

            compactor.DoCompactSnapshot(prev);
            Assert.That(repo.TryLeaseCompactedSnapshotTo(prev, out PersistedSnapshot? compacted), Is.True);
            try
            {
                using PersistedSnapshotBloom bloomLease = bloomManager.LeaseOrSentinel(prev);
                Assert.That(bloomLease, Is.Not.SameAs(PersistedSnapshotBloom.AlwaysTrue), "test needs a real merge-built bloom (shared bloomManager)");
                BloomFilter bloom = bloomLease.Bloom;
                foreach (int a in allAddrs)
                {
                    ulong addrKey = PersistedSnapshotBloomBuilder.AddressKey(DistinctAddress(a));
                    Assert.That(bloom.MightContain(addrKey), Is.True, $"merge bloom MISSING address {a} → bundle would skip it");
                }
                foreach ((int a, ulong id) in allSlots)
                {
                    ulong addrKey = PersistedSnapshotBloomBuilder.AddressKey(DistinctAddress(a));
                    ulong slotKey = PersistedSnapshotBloomBuilder.SlotKey(addrKey, DistinctPrefixSlot((int)id));
                    Assert.That(bloom.MightContain(slotKey), Is.True, $"merge bloom MISSING slot ({a},{id})");
                }
            }
            finally { compacted!.Dispose(); }
        }
        finally
        {
            if (Directory.Exists(testDir)) Directory.Delete(testDir, recursive: true);
        }
    }

    /// <summary>
    /// High-entropy stress over partitioned ADDRESS (0x0A) + partitioned SLOT (0x08) columns,
    /// targeting "random invalid block" style corruption: 40 addresses, each present in all 8
    /// base snapshots (so per-address <c>MergeValues</c> runs with matchCount == 8), with a
    /// per-snapshot account update (newest wins), disjoint accumulating slot ranges, and one
    /// shared conflicting slot per address (newest wins). After compaction every account and
    /// every slot must read back correctly — each read done twice in forward then reverse order
    /// to thrash the 8-way address-bound cache (40 ≫ 8 ways) and surface any stale-cache or
    /// mis-recorded-entry bug.
    /// </summary>
    [Test]
    public void Compact_RichOverlappingState_PartitionedColumns_AllReadsCorrect()
    {
        const int A = 40;   // 40 × 20 = 800 key bytes ≫ 200 threshold ⇒ 0x0A address column
        const int S = 8;    // one merge fires at minCompactSize = CompactSize*2 = 8
        const int slotsPerSnap = 4;
        string testDir = Path.Combine(Path.GetTempPath(), $"nethermind_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(testDir);
        try
        {
            using ArenaManager smallArena = new(Path.Combine(testDir, "arenas", "base"), 0, maxArenaSize: 8 * 1024 * 1024);
            using BlobArenaManager smallBlobs = new(Path.Combine(testDir, "blobs", "small"), 8 * 1024 * 1024, PersistedSnapshotTier.Persisted);
            FlatDbConfig config = new()
            {
                CompactSize = 4,
                MinCompactSize = 2,
                PersistedSnapshotSlotPartitionThresholdBytes = 200,
                PersistedSnapshotSlotHashtableMinBytes = 0,
            };
            using PersistedSnapshotRepository repo = new(smallArena, smallBlobs, new MemDb(), config, new PersistedSnapshotBloomFilterManager(), LimboLogs.Instance);
            repo.LoadFromCatalog();
            PersistedSnapshotCompactor compactor = new(
                repo, smallArena, config, ScheduleHelper.CreateWithOffset(config, 0),
                LimboLogs.Instance, new PersistedSnapshotBloomFilterManager(),
                minCompactSize: config.CompactSize * 2, maxCompactSize: config.PersistedSnapshotMaxCompactSize);

            Dictionary<int, UInt256> expectBalance = [];
            Dictionary<(int a, ulong id), byte[]> expectSlot = [];

            static UInt256 SlotKey(int a, ulong id) => DistinctPrefixSlot((int)((ulong)a * 1_000_000 + id));
            static byte[] SlotVal(int s, int a, int i) => [(byte)s, (byte)a, (byte)i, 0xAB];

            StateId prev = new(0, Keccak.EmptyTreeHash);
            for (int s = 1; s <= S; s++)
            {
                StateId next = new(s, Keccak.Compute($"s{s}"));
                SnapshotContent c = new();
                for (int a = 0; a < A; a++)
                {
                    Address addr = DistinctAddress(a + 1);
                    UInt256 bal = (UInt256)(s * 1000 + a);
                    c.Accounts[addr] = Build.An.Account.WithBalance(bal).TestObject;
                    expectBalance[a] = bal; // newest wins

                    // Every 3rd address is account-only (no storage) — exercises the no-slots
                    // up-front Add path interleaved with the streaming path inside the same
                    // partitioned address builder.
                    if (a % 3 == 0)
                        continue;

                    // Disjoint accumulating range: globalId unique per (a, s, i).
                    for (int i = 0; i < slotsPerSnap; i++)
                    {
                        ulong id = (ulong)((s - 1) * slotsPerSnap + i);
                        byte[] v = SlotVal(s, a, i);
                        c.Storages[(addr, SlotKey(a, id))] = new SlotValue(v);
                        expectSlot[(a, id)] = v;
                    }
                    // Shared conflicting slot (id 99999): newest snapshot wins.
                    byte[] sv = [(byte)s, (byte)a, 0xFF, 0xCD];
                    c.Storages[(addr, SlotKey(a, 99999))] = new SlotValue(sv);
                    expectSlot[(a, 99999)] = sv;
                }
                repo.ConvertSnapshotToPersistedSnapshot(new Snapshot(prev, next, c, _pool, ResourcePool.Usage.MainBlockProcessing)).Dispose();
                prev = next;
            }

            compactor.DoCompactSnapshot(prev);
            Assert.That(repo.TryLeaseCompactedSnapshotTo(prev, out PersistedSnapshot? compacted), Is.True);
            try
            {
                // Read every account + slot twice, forward then reverse, to thrash the cache.
                foreach (bool reverse in new[] { false, true })
                {
                    for (int idx = 0; idx < A; idx++)
                    {
                        int a = reverse ? A - 1 - idx : idx;
                        Address addr = DistinctAddress(a + 1);
                        Assert.That(compacted!.TryGetAccount(addr, out Account? acc), Is.True, $"account {a} missing (reverse={reverse})");
                        Assert.That(acc!.Balance, Is.EqualTo(expectBalance[a]), $"account {a} balance (reverse={reverse})");

                        foreach (((int ea, ulong id), byte[] v) in expectSlot)
                        {
                            if (ea != a) continue;
                            SlotValue slot = default;
                            Assert.That(compacted.TryGetSlot(addr, SlotKey(a, id), ref slot), Is.True, $"slot ({a},{id}) missing (reverse={reverse})");
                            Assert.That(slot.AsReadOnlySpan.ToArray(), Is.EqualTo(new SlotValue(v).AsReadOnlySpan.ToArray()), $"slot ({a},{id}) value (reverse={reverse})");
                        }
                    }
                }
            }
            finally { compacted!.Dispose(); }
        }
        finally
        {
            if (Directory.Exists(testDir)) Directory.Delete(testDir, recursive: true);
        }
    }

    /// <summary>
    /// The persistence walk (PersistenceManager.PersistSnapshot → PersistedSnapshotScanner) over a
    /// partitioned ADDRESS column (0x0A) must yield the correct address, account, and per-slot
    /// VALUES — not just the right keys. A wrong/missing value here is written verbatim to the
    /// backing store and resurfaces as a "random invalid block" after the snapshot is persisted.
    /// Builds a 0x0A snapshot with distinct balances + slot values, scans it exactly as the
    /// persistence path does, and asserts every yielded value (and the exact address/slot counts).
    /// </summary>
    // 0x0A = multi-partition (threshold 200), 0x0B = single partition + hashtable (the common
    // production case for compacted address columns, threshold 4 MiB), both with htMin 0.
    [TestCase(200L, IndexType.PartitionedBTree)]
    [TestCase(4L * 1024 * 1024, IndexType.SinglePartitionHashtableBTree)]
    public void Scan_PartitionedAddressColumn_YieldsCorrectAccountAndSlotValues(long thresholdBytes, IndexType expectedTail)
    {
        const int A = 50;
        string testDir = Path.Combine(Path.GetTempPath(), $"nethermind_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(testDir);
        try
        {
            using ArenaManager smallArena = new(Path.Combine(testDir, "arenas", "base"), 0, maxArenaSize: 8 * 1024 * 1024);
            using BlobArenaManager smallBlobs = new(Path.Combine(testDir, "blobs", "small"), 8 * 1024 * 1024, PersistedSnapshotTier.Persisted);
            FlatDbConfig config = new()
            {
                PersistedSnapshotSlotPartitionThresholdBytes = thresholdBytes,
                PersistedSnapshotSlotHashtableMinBytes = 0,
            };
            using PersistedSnapshotRepository repo = new(smallArena, smallBlobs, new MemDb(), config, new PersistedSnapshotBloomFilterManager(), LimboLogs.Instance);
            repo.LoadFromCatalog();

            Dictionary<int, UInt256> expectBalance = [];
            Dictionary<(int a, int i), byte[]> expectSlot = [];
            SnapshotContent c = new();
            for (int a = 0; a < A; a++)
            {
                Address addr = DistinctAddress(a + 1);
                UInt256 bal = (UInt256)(a * 7 + 13);
                c.Accounts[addr] = Build.An.Account.WithBalance(bal).TestObject;
                expectBalance[a] = bal;
                int slotCount = a % 4 == 0 ? 0 : (a % 7) + 1; // some addresses have no slots
                for (int i = 0; i < slotCount; i++)
                {
                    byte[] v = [(byte)(a + 1), (byte)(i + 1), 0x9E];
                    c.Storages[(addr, DistinctPrefixSlot(a * 1000 + i))] = new SlotValue(v);
                    expectSlot[(a, i)] = new SlotValue(v).AsReadOnlySpan.ToArray();
                }
            }

            PersistedSnapshot snap = repo.ConvertSnapshotToPersistedSnapshot(
                new Snapshot(new StateId(0, Keccak.EmptyTreeHash), new StateId(1, Keccak.Compute("s1")), c, _pool, ResourcePool.Usage.MainBlockProcessing));
            try
            {
                // Confirm the column is partitioned 0x0A, then scan exactly as persistence does.
                using WholeReadSession tailSession = snap.BeginWholeReadSession();
                WholeReadSessionReader tailReader = tailSession.GetReader();
                Assert.That(PersistedSnapshotReader.TryGetAddressColumnBound<WholeReadSessionReader, NoOpPin>(in tailReader, out Bound addrCol), Is.True);
                Span<byte> tb = stackalloc byte[1];
                tailReader.TryRead(addrCol.Offset + addrCol.Length - 1, tb);
                Assert.That((IndexType)tb[0], Is.EqualTo(expectedTail), $"column must be {expectedTail}");

                int seenAddrs = 0;
                Dictionary<int, UInt256> gotBalance = [];
                Dictionary<(int, int), byte[]> gotSlot = [];
                Dictionary<int, int> slotCountByAddr = [];
                using WholeReadSession session = snap.BeginWholeReadSession();
                PersistedSnapshotScanner scanner = new(session, snap);
                foreach (PersistedSnapshotScanner.PerAddressEntry entry in scanner.PerAddresses)
                {
                    seenAddrs++;
                    // Recover the address index from the last 4 key bytes.
                    int a = (int)BinaryPrimitives.ReadUInt32BigEndian(entry.Address.Bytes.Slice(16)) - 1;
                    Assert.That(entry.HasAccount, Is.True, $"address {a} account missing in scan");
                    gotBalance[a] = entry.Account!.Balance;
                    int sc = 0;
                    foreach (PersistedSnapshotScanner.SlotEntry slot in entry.Slots)
                    {
                        // Recover slot index i from the slot key's encoded id.
                        ulong id = (ulong)(slot.Slot >> 16);
                        int i = (int)(id - (ulong)(a * 1000));
                        gotSlot[(a, i)] = slot.Value!.Value.AsReadOnlySpan.ToArray();
                        sc++;
                    }
                    slotCountByAddr[a] = sc;
                }

                Assert.That(seenAddrs, Is.EqualTo(A), "scan must yield every address exactly once");
                Assert.That(gotBalance, Is.EquivalentTo(expectBalance), "account balances from scan must match");
                Assert.That(gotSlot.Count, Is.EqualTo(expectSlot.Count), "slot count from scan must match");
                foreach (((int a, int i), byte[] v) in expectSlot)
                    Assert.That(gotSlot[(a, i)], Is.EqualTo(v), $"slot ({a},{i}) value from scan");
            }
            finally { snap.Dispose(); }
        }
        finally
        {
            if (Directory.Exists(testDir)) Directory.Delete(testDir, recursive: true);
        }
    }

    /// <summary>
    /// Concurrent reads over a partitioned ADDRESS column (0x0A): many threads hammer the
    /// lock-free 8-way address-bound cache (eviction + refill races) while reading accounts and
    /// slots. Every read must return the correct value on every thread — a data race in the
    /// cache or the partitioned resolver would surface here as a "random" wrong/missing read.
    /// </summary>
    [Test]
    public void Read_PartitionedAddressColumn_Concurrent_AllReadsCorrect()
    {
        const int A = 64;
        string testDir = Path.Combine(Path.GetTempPath(), $"nethermind_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(testDir);
        try
        {
            using ArenaManager smallArena = new(Path.Combine(testDir, "arenas", "base"), 0, maxArenaSize: 8 * 1024 * 1024);
            using BlobArenaManager smallBlobs = new(Path.Combine(testDir, "blobs", "small"), 8 * 1024 * 1024, PersistedSnapshotTier.Persisted);
            FlatDbConfig config = new()
            {
                PersistedSnapshotSlotPartitionThresholdBytes = 200,
                PersistedSnapshotSlotHashtableMinBytes = 0,
            };
            using PersistedSnapshotRepository repo = new(smallArena, smallBlobs, new MemDb(), config, new PersistedSnapshotBloomFilterManager(), LimboLogs.Instance);
            repo.LoadFromCatalog();

            SnapshotContent c = new();
            for (int a = 0; a < A; a++)
            {
                Address addr = DistinctAddress(a + 1);
                c.Accounts[addr] = Build.An.Account.WithBalance((UInt256)(a + 1)).TestObject;
                for (int i = 0; i < 6; i++)
                    c.Storages[(addr, DistinctPrefixSlot(a * 1000 + i))] = new SlotValue([(byte)a, (byte)i, 0x5A]);
            }

            PersistedSnapshot snap = repo.ConvertSnapshotToPersistedSnapshot(
                new Snapshot(new StateId(0, Keccak.EmptyTreeHash), new StateId(1, Keccak.Compute("s1")), c, _pool, ResourcePool.Usage.MainBlockProcessing));
            try
            {
                System.Collections.Concurrent.ConcurrentQueue<string> failures = new();
                Parallel.For(0, 16, new ParallelOptions { MaxDegreeOfParallelism = 16 }, _ =>
                {
                    for (int rep = 0; rep < 50; rep++)
                    {
                        for (int a = 0; a < A; a++)
                        {
                            Address addr = DistinctAddress(a + 1);
                            if (!snap.TryGetAccount(addr, out Account? acc) || acc!.Balance != (UInt256)(a + 1))
                                failures.Enqueue($"account {a}");
                            for (int i = 0; i < 6; i++)
                            {
                                SlotValue slot = default;
                                byte[] want = new SlotValue([(byte)a, (byte)i, 0x5A]).AsReadOnlySpan.ToArray();
                                if (!snap.TryGetSlot(addr, DistinctPrefixSlot(a * 1000 + i), ref slot)
                                    || !slot.AsReadOnlySpan.ToArray().AsSpan().SequenceEqual(want))
                                    failures.Enqueue($"slot ({a},{i})");
                            }
                        }
                    }
                });
                Assert.That(failures, Is.Empty, $"concurrent reads returned {failures.Count} wrong/missing results, e.g. {(failures.TryPeek(out string? f) ? f : "")}");
            }
            finally { snap.Dispose(); }
        }
        finally
        {
            if (Directory.Exists(testDir)) Directory.Delete(testDir, recursive: true);
        }
    }

    /// <summary>
    /// Persistence-side iteration over a partitioned ADDRESS column (0x0A): the bloom-rebuild
    /// scan (<see cref="PersistedSnapshotBloomBuilder"/> → <see cref="PersistedSnapshotScanner"/>
    /// → <see cref="HsstRefEnumerator{TReader,TPin}"/>) must walk every address entry. Builds a
    /// partitioned-address base snapshot, rebuilds its bloom by scanning, and asserts every
    /// address + slot key is present — i.e. the enumerator visited all partitions.
    /// </summary>
    [Test]
    public void Scan_PartitionedAddressColumn_BloomRebuild_VisitsAllEntries()
    {
        const int addrCount = 64; // 64 × 20 = 1280 key bytes ≫ 200-byte threshold ⇒ multi-partition 0x0A
        string testDir = Path.Combine(Path.GetTempPath(), $"nethermind_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(testDir);
        try
        {
            using ArenaManager smallArena = new(Path.Combine(testDir, "arenas", "base"), 0, maxArenaSize: 4 * 1024 * 1024);
            using BlobArenaManager smallBlobs = new(Path.Combine(testDir, "blobs", "small"), 4 * 1024 * 1024, PersistedSnapshotTier.Persisted);
            FlatDbConfig config = new()
            {
                PersistedSnapshotSlotPartitionThresholdBytes = 200,
                PersistedSnapshotSlotHashtableMinBytes = 0,
            };
            using PersistedSnapshotRepository repo = new(smallArena, smallBlobs, new MemDb(), config, new PersistedSnapshotBloomFilterManager(), LimboLogs.Instance);
            repo.LoadFromCatalog();

            SnapshotContent c = new();
            for (int id = 1; id <= addrCount; id++)
            {
                Address addr = DistinctAddress(id);
                c.Accounts[addr] = Build.An.Account.WithBalance((UInt256)id).TestObject;
                c.Storages[(addr, DistinctPrefixSlot(id))] = new SlotValue(DistinctPrefixSlotValue(id));
            }

            PersistedSnapshot baseSnap = repo.ConvertSnapshotToPersistedSnapshot(
                new Snapshot(new StateId(0, Keccak.EmptyTreeHash), new StateId(1, Keccak.Compute("s1")), c, _pool, ResourcePool.Usage.MainBlockProcessing));
            try
            {
                using WholeReadSession session = baseSnap.BeginWholeReadSession();
                WholeReadSessionReader reader = session.GetReader();

                // Precondition: the address column really is partitioned (0x0A), so the scan
                // exercises the partitioned enumerator rather than the plain 0x01 walk.
                Assert.That(PersistedSnapshotReader.TryGetAddressColumnBound<WholeReadSessionReader, NoOpPin>(
                    in reader, out Bound addrCol), Is.True);
                Span<byte> tailByte = stackalloc byte[1];
                Assert.That(reader.TryRead(addrCol.Offset + addrCol.Length - 1, tailByte), Is.True);
                Assert.That((IndexType)tailByte[0], Is.EqualTo(IndexType.PartitionedBTree), "address column must be multi-partition 0x0A");

                // Explicitly confirm there really is more than one partition: the 0x0A blob's
                // top-level tree IS the directory (key-first), one entry per partition.
                int partitions = 0;
                HsstBTreeEnumerator<WholeReadSessionReader, NoOpPin> dir = new(in reader, addrCol, keyFirst: true);
                while (dir.MoveNext(in reader)) partitions++;
                Assert.That(partitions, Is.GreaterThan(1), "test must span multiple address partitions");

                // The persistence bloom-rebuild scan must visit every address (and its slot).
                BloomFilter rebuilt = PersistedSnapshotBloomBuilder.Build(session, baseSnap, bitsPerKey: 12.0);
                for (int id = 1; id <= addrCount; id++)
                {
                    Address addr = DistinctAddress(id);
                    ulong addrKey = PersistedSnapshotBloomBuilder.AddressKey(addr);
                    Assert.That(rebuilt.MightContain(addrKey), Is.True, $"address {id} not visited by the scan");
                    Assert.That(rebuilt.MightContain(PersistedSnapshotBloomBuilder.SlotKey(addrKey, DistinctPrefixSlot(id))), Is.True, $"slot {id} not visited by the scan");
                }
            }
            finally { baseSnap.Dispose(); }
        }
        finally
        {
            if (Directory.Exists(testDir)) Directory.Delete(testDir, recursive: true);
        }
    }

    /// <summary>
    /// End-to-end of the partitioned ADDRESS column (key-after-value 0x0A / 0x0B) on the
    /// COMPACTION path. A low address-partition threshold + zero hashtable-min (both reuse the
    /// slot-options config) forces the merged 64-address column through
    /// <c>NWayMergePartitioned</c> into a hashtable-bearing partitioned layout. Every account
    /// and slot must read back through the compacted snapshot's real reader, and the column's
    /// tail byte must be a partitioned index type — confirming the merge actually partitioned
    /// rather than silently degrading to a plain 0x01.
    /// </summary>
    [Test]
    public void Compact_PartitionedAddressColumn_RoundTrips_ThroughPartitionedHsst()
    {
        const int n = 8;                 // power of 2 ≥ minCompactSize (CompactSize*2 = 8) ⇒ one merge
        const int addrsPerSnapshot = 8;  // 8 × 8 = 64 distinct addresses ⇒ 1280 key bytes ≫ threshold
        string testDir = Path.Combine(Path.GetTempPath(), $"nethermind_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(testDir);
        try
        {
            using ArenaManager smallArena = new(Path.Combine(testDir, "arenas", "base"), 0, maxArenaSize: 4 * 1024 * 1024);
            using BlobArenaManager smallBlobs = new(Path.Combine(testDir, "blobs", "small"), 4 * 1024 * 1024, PersistedSnapshotTier.Persisted);
            // Low partition threshold + zero hashtable-min ⇒ the address column partitions and
            // every partition carries a hashtable (0x0A multi / 0x0B single).
            FlatDbConfig config = new()
            {
                CompactSize = 4,
                MinCompactSize = 2,
                PersistedSnapshotSlotPartitionThresholdBytes = 200,
                PersistedSnapshotSlotHashtableMinBytes = 0,
            };
            using PersistedSnapshotRepository repo = new(smallArena, smallBlobs, new MemDb(), config, new PersistedSnapshotBloomFilterManager(), LimboLogs.Instance);
            repo.LoadFromCatalog();

            PersistedSnapshotCompactor compactor = new(
                repo, smallArena, config,
                ScheduleHelper.CreateWithOffset(config, 0),
                LimboLogs.Instance, new PersistedSnapshotBloomFilterManager(),
                minCompactSize: config.CompactSize * 2,
                maxCompactSize: config.PersistedSnapshotMaxCompactSize);

            StateId prev = new(0, Keccak.EmptyTreeHash);
            for (int i = 1; i <= n; i++)
            {
                StateId next = new(i, Keccak.Compute($"s{i}"));
                SnapshotContent c = new();
                for (int k = 0; k < addrsPerSnapshot; k++)
                {
                    int id = (i - 1) * addrsPerSnapshot + k + 1;
                    Address addr = DistinctAddress(id);
                    c.Accounts[addr] = Build.An.Account.WithBalance((UInt256)id).TestObject;
                    c.Storages[(addr, DistinctPrefixSlot(id))] = new SlotValue(DistinctPrefixSlotValue(id));
                }
                repo.ConvertSnapshotToPersistedSnapshot(new Snapshot(prev, next, c, _pool, ResourcePool.Usage.MainBlockProcessing)).Dispose();
                prev = next;
            }

            compactor.DoCompactSnapshot(prev);

            Assert.That(repo.TryLeaseCompactedSnapshotTo(prev, out PersistedSnapshot? compacted), Is.True);
            try
            {
                // The address column must be a partitioned layout, not a plain 0x01 BTree.
                using (WholeReadSession session = compacted!.BeginWholeReadSession())
                {
                    WholeReadSessionReader reader = session.GetReader();
                    Assert.That(PersistedSnapshotReader.TryGetAddressColumnBound<WholeReadSessionReader, NoOpPin>(
                        in reader, out Bound addrCol), Is.True);
                    Span<byte> tail = stackalloc byte[1];
                    Assert.That(reader.TryRead(addrCol.Offset + addrCol.Length - 1, tail), Is.True);
                    Assert.That((IndexType)tail[0],
                        Is.EqualTo(IndexType.PartitionedBTree).Or.EqualTo(IndexType.SinglePartitionHashtableBTree),
                        "compacted address column must be partitioned (0x0A/0x0B)");
                }

                for (int id = 1; id <= n * addrsPerSnapshot; id++)
                {
                    Address addr = DistinctAddress(id);
                    Assert.That(compacted!.TryGetAccount(addr, out Account? acc), Is.True, $"account {id} missing after compaction");
                    Assert.That(acc!.Balance, Is.EqualTo((UInt256)id), $"account {id} balance mismatch");
                    SlotValue slot = default;
                    Assert.That(compacted.TryGetSlot(addr, DistinctPrefixSlot(id), ref slot), Is.True, $"slot {id} missing after compaction");
                    Assert.That(slot.AsReadOnlySpan.ToArray(), Is.EqualTo(new SlotValue(DistinctPrefixSlotValue(id)).AsReadOnlySpan.ToArray()), $"slot {id} value mismatch");
                }
                // An address that was never written must be absent.
                Assert.That(compacted!.TryGetAccount(DistinctAddress(n * addrsPerSnapshot + 1000), out _), Is.False);
            }
            finally { compacted!.Dispose(); }
        }
        finally
        {
            if (Directory.Exists(testDir)) Directory.Delete(testDir, recursive: true);
        }
    }

    /// <summary>
    /// Verifies the configurable upper/lower partition thresholds actually plumb through
    /// <c>IFlatDbConfig</c> into the base build: a very low
    /// <c>PersistedSnapshotSlotPartitionThresholdBytes</c> (plus
    /// <c>PersistedSnapshotSlotHashtableMinBytes = 0</c>) forces a 100-distinct-prefix contract
    /// into a hashtable-bearing partitioned (0x08) layout, and every slot still round-trips
    /// through the real mmap reader. The config value reaching the builder without breaking
    /// reads is the behavior under test.
    /// </summary>
    [Test]
    public void Config_LowSlotPartitionThreshold_PartitionsAndRoundTrips()
    {
        const int slotCount = 100;
        string testDir = Path.Combine(Path.GetTempPath(), $"nethermind_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(testDir);
        try
        {
            // 100 × 30-byte prefixes = 3000 bytes ≫ 512-byte upper threshold ⇒ several partitions.
            FlatDbConfig config = new()
            {
                PersistedSnapshotSlotPartitionThresholdBytes = 512,
                PersistedSnapshotSlotHashtableMinBytes = 0,
            };
            using ArenaManager smallArena = new(Path.Combine(testDir, "arenas", "base"), 0, maxArenaSize: 4 * 1024 * 1024);
            using BlobArenaManager smallBlobs = new(Path.Combine(testDir, "blobs", "small"), 4 * 1024 * 1024, PersistedSnapshotTier.Persisted);
            using PersistedSnapshotRepository repo = new(smallArena, smallBlobs, new MemDb(), config, new PersistedSnapshotBloomFilterManager(), LimboLogs.Instance);
            repo.LoadFromCatalog();

            SnapshotContent c = new();
            c.Accounts[TestItem.AddressA] = Build.An.Account.WithBalance(1).TestObject;
            for (int id = 1; id <= slotCount; id++)
                c.Storages[(TestItem.AddressA, DistinctPrefixSlot(id))] = new SlotValue(DistinctPrefixSlotValue(id));

            PersistedSnapshot baseSnap = repo.ConvertSnapshotToPersistedSnapshot(
                new Snapshot(new StateId(0, Keccak.EmptyTreeHash), new StateId(1, Keccak.Compute("s1")), c, _pool, ResourcePool.Usage.MainBlockProcessing));
            try
            {
                for (int id = 1; id <= slotCount; id++)
                {
                    SlotValue slot = default;
                    Assert.That(baseSnap.TryGetSlot(TestItem.AddressA, DistinctPrefixSlot(id), ref slot), Is.True, $"slot {id} missing");
                    Assert.That(slot.AsReadOnlySpan.ToArray(), Is.EqualTo(new SlotValue(DistinctPrefixSlotValue(id)).AsReadOnlySpan.ToArray()), $"slot {id} value mismatch");
                }
            }
            finally { baseSnap.Dispose(); }
        }
        finally
        {
            if (Directory.Exists(testDir)) Directory.Delete(testDir, recursive: true);
        }
    }

    /// <summary>
    /// Regression for the matchCount==1 byte-copy fast path in NWayMergePerAddressColumn.
    /// Each successful <c>HsstReader.TrySeek</c> narrows the reader's internal bound to
    /// the matched sub-tag's value scope, so sibling sub-tag seeks must reset the bound
    /// between calls — otherwise only the first hit (SlotSubTag) succeeds and the three
    /// storage-trie sub-tag bloom adds silently never run, even though the underlying
    /// nodes ride along in the byte-copied per-address blob. We pack AddressA into one
    /// source with slots plus storage-trie nodes at every depth tier (top / compact /
    /// fallback) and pair it with an unrelated address in the second source so that
    /// matchCount==1 for AddressA. The bloom manager is shared with the compactor so
    /// <c>bloomCapacity</c> is non-zero and the merger produces a real (non-AlwaysTrue)
    /// bloom we can probe.
    /// </summary>
    [Test]
    public void Compact_ByteCopyFastPath_AddsAllSubTagBloomKeys()
    {
        string testDir = Path.Combine(Path.GetTempPath(), $"nethermind_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(testDir);
        try
        {
            using ArenaManager smallArena = new(Path.Combine(testDir, "arenas", "base"), 0, maxArenaSize: 64 * 1024);
            using BlobArenaManager smallBlobs = new(Path.Combine(testDir, "blobs", "small"), 1024 * 1024, PersistedSnapshotTier.Persisted);
            using PersistedSnapshotBloomFilterManager bloomManager = new();
            using PersistedSnapshotRepository repo = new(smallArena, smallBlobs, new MemDb(), new FlatDbConfig(), bloomManager, LimboLogs.Instance);
            repo.LoadFromCatalog();

            IFlatDbConfig config = new FlatDbConfig { CompactSize = 1, MinCompactSize = 2 };
            PersistedSnapshotCompactor compactor = new(
                repo, smallArena, config, ScheduleHelper.CreateWithOffset(config, 0),
                Nethermind.Logging.LimboLogs.Instance, bloomManager,
                minCompactSize: 2, maxCompactSize: 2);

            Hash256 addrHash256 = Keccak.Compute(TestItem.AddressA.Bytes);
            TreePath topPath = new(Keccak.Compute("trie_top"), 4);          // → StorageTopSubTag (4-byte key)
            TreePath compactPath = new(Keccak.Compute("trie_compact"), 10); // → StorageCompactSubTag (8-byte key)
            TreePath fallbackPath = new(Keccak.Compute("trie_fb"), 20);     // → StorageFallbackSubTag (33-byte key)
            UInt256 slotIndex = 7;

            SnapshotContent c0 = new();
            c0.Accounts[TestItem.AddressA] = Build.An.Account.WithBalance(100).TestObject;
            c0.Storages[(TestItem.AddressA, slotIndex)] = new SlotValue(new byte[] { 0x42 });
            c0.StorageNodes[(addrHash256, topPath)] = new TrieNode(NodeType.Leaf, [0xC1, 0x80]);
            c0.StorageNodes[(addrHash256, compactPath)] = new TrieNode(NodeType.Leaf, [0xC1, 0x81]);
            c0.StorageNodes[(addrHash256, fallbackPath)] = new TrieNode(NodeType.Leaf, [0xC1, 0x82]);

            // Different address in the second source so AddressA has matchCount==1 (triggers
            // the per-address byte-copy fast path) while still having ≥ 2 sources to compact.
            SnapshotContent c1 = new();
            c1.Accounts[TestItem.AddressB] = Build.An.Account.WithBalance(200).TestObject;

            StateId s0 = new(0, Keccak.EmptyTreeHash);
            StateId s1 = new(1, Keccak.Compute("s1"));
            StateId s2 = new(2, Keccak.Compute("s2"));
            repo.ConvertSnapshotToPersistedSnapshot(new Snapshot(s0, s1, c0, _pool, ResourcePool.Usage.MainBlockProcessing)).Dispose();
            repo.ConvertSnapshotToPersistedSnapshot(new Snapshot(s1, s2, c1, _pool, ResourcePool.Usage.MainBlockProcessing)).Dispose();

            compactor.DoCompactSnapshot(s2);

            Assert.That(repo.TryLeaseCompactedSnapshotTo(s2, out PersistedSnapshot? compacted), Is.True);
            using (compacted)
            {
                using PersistedSnapshotBloom bloomLease = bloomManager.LeaseOrSentinel(s2);
                Assert.That(bloomLease, Is.Not.SameAs(PersistedSnapshotBloom.AlwaysTrue),
                    "Compacted snapshot must have a real bloom — test requires shared bloomManager so bloomCapacity > 0");

                BloomFilter bloom = bloomLease.Bloom;
                ValueHash256 addrHash = ValueKeccak.Compute(TestItem.AddressA.Bytes);
                ulong addrKey = PersistedSnapshotBloomBuilder.AddressKey(TestItem.AddressA);

                Assert.Multiple(() =>
                {
                    Assert.That(bloom.MightContain(addrKey), Is.True, "Address key");
                    Assert.That(bloom.MightContain(PersistedSnapshotBloomBuilder.SlotKey(addrKey, slotIndex)), Is.True, "Slot key");
                    Assert.That(bloom.MightContain(PersistedSnapshotBloomBuilder.StorageNodeKey(in addrHash, in topPath)), Is.True,
                        "Storage-trie top — fails when sibling TrySeek bound isn't reset between sub-tag seeks");
                    Assert.That(bloom.MightContain(PersistedSnapshotBloomBuilder.StorageNodeKey(in addrHash, in compactPath)), Is.True,
                        "Storage-trie compact");
                    Assert.That(bloom.MightContain(PersistedSnapshotBloomBuilder.StorageNodeKey(in addrHash, in fallbackPath)), Is.True,
                        "Storage-trie fallback");
                });
            }
        }
        finally
        {
            if (Directory.Exists(testDir))
                Directory.Delete(testDir, recursive: true);
        }
    }

    /// <summary>
    /// Regression for the 4 KiB page-alignment pad inserted in the
    /// <c>matchCount == 1</c> fast path of <c>NWayMergePerAddressColumn</c>. The pad
    /// pushes an about-to-straddle inner-HSST blob onto a fresh page so it lives in
    /// one OS page; the leading pad bytes must be inert — recorded as gap data via
    /// <c>FinishValueWrite(key, vb.Length)</c> rather than absorbed into the value
    /// range, otherwise the outer leaf's <c>ValueStart = MetadataStart − ValueLength</c>
    /// derivation would land in the pad and decoding would fail. Drives many
    /// distinct addresses through the fast path with non-trivial inner HSSTs (slots
    /// + a storage-trie node each) so positions sweep across multiple page
    /// boundaries — at least some inner HSSTs will trigger the pad code path, and
    /// all must round-trip read intact post-compaction.
    /// </summary>
    [TestCase(40)]
    [TestCase(120)]
    public void Compact_ByteCopyFastPath_PageAlignPaddingPreservesValues(int accountCount)
    {
        string testDir = Path.Combine(Path.GetTempPath(), $"nethermind_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(testDir);
        try
        {
            using ArenaManager smallArena = new(Path.Combine(testDir, "arenas", "base"), 0, maxArenaSize: 256 * 1024);
            using BlobArenaManager smallBlobs = new(Path.Combine(testDir, "blobs", "small"), 4 * 1024 * 1024, PersistedSnapshotTier.Persisted);
            using PersistedSnapshotRepository repo = new(smallArena, smallBlobs, new MemDb(), new FlatDbConfig(), new PersistedSnapshotBloomFilterManager(), LimboLogs.Instance);
            repo.LoadFromCatalog();

            IFlatDbConfig config = new FlatDbConfig { CompactSize = 1, MinCompactSize = 2 };
            PersistedSnapshotCompactor compactor = new(
                repo, smallArena, config, ScheduleHelper.CreateWithOffset(config, 0),
                Nethermind.Logging.LimboLogs.Instance, new PersistedSnapshotBloomFilterManager(),
                minCompactSize: 2, maxCompactSize: 2);

            // Source 0: accountCount addresses with varying slot counts so inner-HSST
            // sizes span ~tens to ~hundreds of bytes — repeated fast-path writes
            // sweep across 4 KiB page boundaries in the destination arena.
            SnapshotContent c0 = new();
            for (int i = 0; i < accountCount; i++)
            {
                Address addr = TestItem.Addresses[i];
                c0.Accounts[addr] = Build.An.Account.WithBalance((UInt256)(i + 1)).TestObject;
                int slots = 1 + (i % 7);
                for (int s = 0; s < slots; s++)
                    c0.Storages[(addr, (UInt256)(s + 1))] = new SlotValue(new byte[] { (byte)((i * 13 + s) & 0xFF) });
                c0.StorageNodes[(Keccak.Compute(addr.Bytes), new TreePath(Keccak.Compute($"p{i}"), 4))]
                    = new TrieNode(NodeType.Leaf, [0xC1, (byte)(i & 0xFF)]);
            }

            // Source 1: a single unrelated address so matchCount == 1 for every
            // address in source 0 (drives them all through the fast path).
            SnapshotContent c1 = new();
            c1.Accounts[TestItem.AddressB] = Build.An.Account.WithBalance(999).TestObject;

            StateId s0 = new(0, Keccak.EmptyTreeHash);
            StateId s1 = new(1, Keccak.Compute("p1"));
            StateId s2 = new(2, Keccak.Compute("p2"));
            repo.ConvertSnapshotToPersistedSnapshot(new Snapshot(s0, s1, c0, _pool, ResourcePool.Usage.MainBlockProcessing)).Dispose();
            repo.ConvertSnapshotToPersistedSnapshot(new Snapshot(s1, s2, c1, _pool, ResourcePool.Usage.MainBlockProcessing)).Dispose();

            compactor.DoCompactSnapshot(s2);

            Assert.That(repo.TryLeaseCompactedSnapshotTo(s2, out PersistedSnapshot? compacted), Is.True);
            using (compacted)
            {
                Assert.Multiple(() =>
                {
                    for (int i = 0; i < accountCount; i++)
                    {
                        Address addr = TestItem.Addresses[i];
                        Assert.That(compacted!.TryGetAccount(addr, out Account? a), Is.True,
                            $"Account {i} must survive fast-path compaction");
                        Assert.That(a!.Balance, Is.EqualTo((UInt256)(i + 1)),
                            $"Account {i} balance mismatch — pad bytes leaked into the value range");

                        int slots = 1 + (i % 7);
                        for (int s = 0; s < slots; s++)
                        {
                            SlotValue slot = default;
                            Assert.That(compacted.TryGetSlot(addr, (UInt256)(s + 1), ref slot), Is.True,
                                $"Slot {s + 1} for account {i} must survive fast-path compaction");
                            SlotValue expected = new(new byte[] { (byte)((i * 13 + s) & 0xFF) });
                            Assert.That(slot.AsReadOnlySpan.ToArray(),
                                Is.EqualTo(expected.AsReadOnlySpan.ToArray()),
                                $"Slot value mismatch for account {i} slot {s + 1}");
                        }
                    }
                });
            }
        }
        finally
        {
            if (Directory.Exists(testDir))
                Directory.Delete(testDir, recursive: true);
        }
    }

    /// <summary>
    /// Metadata invariants for the blob-arena layout: base snapshots carry no
    /// <c>noderefs</c> flag and a single <c>ref_ids</c> entry (their own blob arena id);
    /// the compacted snapshot carries the <c>noderefs</c> flag and a <c>ref_ids</c> set
    /// equal to the union of source base-snapshot blob arena ids.
    /// </summary>
    [Test]
    public void CompactedSnapshot_Metadata_NodeRefsFlagAndRefIdsUnion()
    {
        string testDir = Path.Combine(Path.GetTempPath(), $"nethermind_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(testDir);
        try
        {
            using ArenaManager smallArena = new(Path.Combine(testDir, "arenas", "base"), 0, maxArenaSize: 64 * 1024);
            using BlobArenaManager smallBlobs = new(Path.Combine(testDir, "blobs", "small"), 1024 * 1024, PersistedSnapshotTier.Persisted);
            using PersistedSnapshotRepository repo = new(smallArena, smallBlobs, new MemDb(), new FlatDbConfig(), new PersistedSnapshotBloomFilterManager(), LimboLogs.Instance);
            repo.LoadFromCatalog();

            IFlatDbConfig config = new FlatDbConfig { CompactSize = 4, MinCompactSize = 2 };
            PersistedSnapshotCompactor compactor = new(
                repo, smallArena, config,
                ScheduleHelper.CreateWithOffset(config, 0),
                Nethermind.Logging.LimboLogs.Instance, new PersistedSnapshotBloomFilterManager(),
                minCompactSize: config.CompactSize * 2,
                maxCompactSize: config.PersistedSnapshotMaxCompactSize);

            StateId prev = new(0, Keccak.EmptyTreeHash);
            StateId[] states = new StateId[9];
            states[0] = prev;
            HashSet<ushort> baseRefIds = [];
            for (int i = 1; i <= 8; i++)
            {
                states[i] = new StateId(i, Keccak.Compute($"{i}"));
                SnapshotContent c = new();
                c.Accounts[TestItem.Addresses[i - 1]] = Build.An.Account.WithBalance((UInt256)(i * 100)).TestObject;
                c.StateNodes[new TreePath(Keccak.Compute($"path{i}"), 4)] = new TrieNode(NodeType.Leaf, [(byte)(0xC1), (byte)i]);
                repo.ConvertSnapshotToPersistedSnapshot(new Snapshot(prev, states[i], c, _pool, ResourcePool.Usage.MainBlockProcessing)).Dispose();
                prev = states[i];
            }

            for (int i = 1; i <= 8; i++)
            {
                Assert.That(repo.TryLeaseSnapshotTo(states[i], out PersistedSnapshot? baseSnap), Is.True);
                using (baseSnap)
                {
                    using WholeReadSession session = baseSnap!.BeginWholeReadSession();
                    WholeReadSessionReader reader = session.GetReader();
                    ushort[]? ids = PersistedSnapshot.ReadRefIdsFromMetadata<WholeReadSessionReader, NoOpPin>(in reader);
                    Assert.That(ids, Is.Not.Null.And.Length.EqualTo(1),
                        $"Base snapshot {i} must carry exactly one blob-arena ref_id");
                    baseRefIds.Add(ids![0]);
                }
            }

            compactor.DoCompactSnapshot(states[8]);

            Assert.That(repo.TryLeaseCompactedSnapshotTo(states[8], out PersistedSnapshot? compacted), Is.True);
            using (compacted)
            {
                using WholeReadSession session = compacted!.BeginWholeReadSession();
                WholeReadSessionReader reader = session.GetReader();
                ushort[]? mergedIds = PersistedSnapshot.ReadRefIdsFromMetadata<WholeReadSessionReader, NoOpPin>(in reader);
                Assert.That(mergedIds, Is.Not.Null);
                Assert.That(new HashSet<ushort>(mergedIds!), Is.EquivalentTo(baseRefIds),
                    "Compacted ref_ids must equal the union of source base blob-arena ids");
            }
        }
        finally
        {
            if (Directory.Exists(testDir))
                Directory.Delete(testDir, recursive: true);
        }
    }

    private static IEnumerable<TestCaseData> MergeValidationTestCases()
    {
        // Each case yields the input SnapshotContents plus an Action<PersistedSnapshot>
        // that asserts the expected post-compaction read-back state.

        // Basic: two snapshots with overlapping accounts — newer balance wins.
        {
            SnapshotContent c0 = new();
            c0.Accounts[TestItem.AddressA] = Build.An.Account.WithBalance(100).TestObject;
            SnapshotContent c1 = new();
            c1.Accounts[TestItem.AddressA] = Build.An.Account.WithBalance(200).TestObject;
            yield return new TestCaseData(
                (object)new[] { c0, c1 },
                (Action<PersistedSnapshot>)(s =>
                {
                    Assert.That(s.TryGetAccount(TestItem.AddressA, out Account? a), Is.True);
                    Assert.That(a!.Balance, Is.EqualTo((UInt256)200));
                }))
                .SetName("Merge_AccountOverride");
        }

        // Regression: advance-corrupts-minKey bug in NWayPackedArrayMerge (StateTopNodes).
        // snapshot[0] has paths {A, B}, snapshot[1] has only {B} with different RLP.
        {
            TreePath pathA = new(Hash256.Zero, 4);
            TreePath pathB = new(new Hash256("0x1000000000000000000000000000000000000000000000000000000000000000"), 4);
            SnapshotContent c0 = new();
            c0.StateNodes[pathA] = new TrieNode(NodeType.Leaf, [0xC0]);
            c0.StateNodes[pathB] = new TrieNode(NodeType.Leaf, [0xC0]);
            SnapshotContent c1 = new();
            c1.StateNodes[pathB] = new TrieNode(NodeType.Leaf, [0xC1, 0x80]);
            yield return new TestCaseData(
                (object)new[] { c0, c1 },
                (Action<PersistedSnapshot>)(s =>
                {
                    Assert.That(s.TryLoadStateNodeRlp(pathA, out byte[]? rlpA), Is.True);
                    Assert.That(rlpA, Is.EqualTo(new byte[] { 0xC0 }), "State node only in older source must survive");
                    Assert.That(s.TryLoadStateNodeRlp(pathB, out byte[]? rlpB), Is.True);
                    Assert.That(rlpB, Is.EqualTo(new byte[] { 0xC1, 0x80 }), "Overlapping state node — newer RLP must win");
                }))
                .SetName("Merge_AdvanceOrder_StateTopNodes");
        }

        // Regression: same bug in NWayInnerMerge (StorageNodes inner merge).
        {
            Hash256 storageAddr = Keccak.Compute("storageAddr");
            TreePath pathA = new(Hash256.Zero, 8);
            TreePath pathB = new(new Hash256("0x1000000000000000000000000000000000000000000000000000000000000000"), 8);
            SnapshotContent c0 = new();
            c0.StorageNodes[(storageAddr, pathA)] = new TrieNode(NodeType.Leaf, [0xC1, 0x80]);
            c0.StorageNodes[(storageAddr, pathB)] = new TrieNode(NodeType.Leaf, [0xC1, 0x80]);
            SnapshotContent c1 = new();
            c1.StorageNodes[(storageAddr, pathB)] = new TrieNode(NodeType.Leaf, [0xC2, 0x80, 0x81]);
            yield return new TestCaseData(
                (object)new[] { c0, c1 },
                (Action<PersistedSnapshot>)(s =>
                {
                    Assert.That(s.TryLoadStorageNodeRlp(storageAddr.ValueHash256, pathA, out byte[]? rlpA), Is.True);
                    Assert.That(rlpA, Is.EqualTo(new byte[] { 0xC1, 0x80 }), "Storage node only in older source must survive");
                    Assert.That(s.TryLoadStorageNodeRlp(storageAddr.ValueHash256, pathB, out byte[]? rlpB), Is.True);
                    Assert.That(rlpB, Is.EqualTo(new byte[] { 0xC2, 0x80, 0x81 }), "Overlapping storage node — newer RLP must win");
                }))
                .SetName("Merge_AdvanceOrder_StorageNodes");
        }

        // Mixed: all data types across two snapshots.
        {
            Hash256 storageAddr = Keccak.Compute("storageAddr");
            TreePath statePath = new(Keccak.Compute("statePath"), 4);
            TreePath storagePath = new(Hash256.Zero, 4);
            SnapshotContent c0 = new();
            c0.Accounts[TestItem.AddressA] = Build.An.Account.WithBalance(100).TestObject;
            c0.Storages[(TestItem.AddressA, 1)] = new SlotValue(new byte[] { 0x42 });
            c0.SelfDestructedStorageAddresses[TestItem.AddressB] = true;
            c0.StateNodes[statePath] = new TrieNode(NodeType.Leaf, [0xC0, 0x80]);
            c0.StorageNodes[(storageAddr, storagePath)] = new TrieNode(NodeType.Leaf, [0xC1, 0x80]);
            SnapshotContent c1 = new();
            c1.Accounts[TestItem.AddressA] = Build.An.Account.WithBalance((UInt256)200).TestObject;
            c1.Storages[(TestItem.AddressA, 2)] = new SlotValue(new byte[] { 0x99 });
            c1.StateNodes[statePath] = new TrieNode(NodeType.Leaf, [0xC1, 0x80]);
            c1.StorageNodes[(storageAddr, storagePath)] = new TrieNode(NodeType.Leaf, [0xC2, 0x80, 0x81]);
            yield return new TestCaseData(
                (object)new[] { c0, c1 },
                (Action<PersistedSnapshot>)(s =>
                {
                    Assert.That(s.TryGetAccount(TestItem.AddressA, out Account? a), Is.True);
                    Assert.That(a!.Balance, Is.EqualTo((UInt256)200), "Account override");

                    SlotValue slot1 = default;
                    Assert.That(s.TryGetSlot(TestItem.AddressA, 1, ref slot1), Is.True, "Older-only slot must survive (no self-destruct on A)");
                    Assert.That(slot1.AsReadOnlySpan.ToArray(), Is.EqualTo(new SlotValue(new byte[] { 0x42 }).AsReadOnlySpan.ToArray()));

                    SlotValue slot2 = default;
                    Assert.That(s.TryGetSlot(TestItem.AddressA, 2, ref slot2), Is.True);
                    Assert.That(slot2.AsReadOnlySpan.ToArray(), Is.EqualTo(new SlotValue(new byte[] { 0x99 }).AsReadOnlySpan.ToArray()));

                    Assert.That(s.TryGetSelfDestructFlag(TestItem.AddressB), Is.Not.Null,
                        "Self-destruct flag for B (set in c0) must be present after compaction");

                    Assert.That(s.TryLoadStateNodeRlp(statePath, out byte[]? stateRlp), Is.True);
                    Assert.That(stateRlp, Is.EqualTo(new byte[] { 0xC1, 0x80 }), "State node — newer wins");

                    Assert.That(s.TryLoadStorageNodeRlp(storageAddr.ValueHash256, storagePath, out byte[]? storageRlp), Is.True);
                    Assert.That(storageRlp, Is.EqualTo(new byte[] { 0xC2, 0x80, 0x81 }), "Storage node — newer wins");
                }))
                .SetName("Merge_MixedDataTypes");
        }

        // Overlapping state node (newer wins) + non-overlapping accounts (both preserved).
        {
            TreePath path = new(Keccak.Compute("path"), 4);
            SnapshotContent c0 = new();
            c0.StateNodes[path] = new TrieNode(NodeType.Leaf, [0xC0]);
            c0.Accounts[TestItem.AddressA] = Build.An.Account.WithBalance(100).TestObject;
            SnapshotContent c1 = new();
            c1.StateNodes[path] = new TrieNode(NodeType.Leaf, [0xC1, 0x80]);
            c1.Accounts[TestItem.AddressB] = Build.An.Account.WithBalance(200).TestObject;
            yield return new TestCaseData(
                (object)new[] { c0, c1 },
                (Action<PersistedSnapshot>)(s =>
                {
                    Assert.That(s.TryLoadStateNodeRlp(path, out byte[]? rlp), Is.True);
                    Assert.That(rlp, Is.EqualTo(new byte[] { 0xC1, 0x80 }), "Newer state-node RLP wins");
                    Assert.That(s.TryGetAccount(TestItem.AddressA, out Account? a), Is.True);
                    Assert.That(a!.Balance, Is.EqualTo((UInt256)100));
                    Assert.That(s.TryGetAccount(TestItem.AddressB, out Account? b), Is.True);
                    Assert.That(b!.Balance, Is.EqualTo((UInt256)200));
                }))
                .SetName("Merge_NewerOverridesOlder");
        }

        // Two distinct state node paths, both survive merge.
        {
            TreePath p1 = new(Keccak.Compute("path1"), 4);
            TreePath p2 = new(Keccak.Compute("path2"), 4);
            SnapshotContent c0 = new();
            c0.StateNodes[p1] = new TrieNode(NodeType.Leaf, [0xC0]);
            SnapshotContent c1 = new();
            c1.StateNodes[p2] = new TrieNode(NodeType.Leaf, [0xC1, 0x80]);
            yield return new TestCaseData(
                (object)new[] { c0, c1 },
                (Action<PersistedSnapshot>)(s =>
                {
                    Assert.That(s.TryLoadStateNodeRlp(p1, out byte[]? r1), Is.True);
                    Assert.That(r1, Is.EqualTo(new byte[] { 0xC0 }));
                    Assert.That(s.TryLoadStateNodeRlp(p2, out byte[]? r2), Is.True);
                    Assert.That(r2, Is.EqualTo(new byte[] { 0xC1, 0x80 }));
                }))
                .SetName("Merge_PreservesNonOverlapping");
        }

        // Older slot cleared by self-destruct, newer slot + flag preserved.
        {
            SnapshotContent c0 = new();
            c0.Storages[(TestItem.AddressA, 1)] = new SlotValue(new byte[] { 0x42 });
            SnapshotContent c1 = new();
            c1.SelfDestructedStorageAddresses[TestItem.AddressA] = false;
            c1.Storages[(TestItem.AddressA, 2)] = new SlotValue(new byte[] { 0x99 });
            yield return new TestCaseData(
                (object)new[] { c0, c1 },
                (Action<PersistedSnapshot>)(s =>
                {
                    SlotValue slot1 = default;
                    Assert.That(s.TryGetSlot(TestItem.AddressA, 1, ref slot1), Is.False, "Older slot must be cleared by newer destruct");
                    SlotValue slot2 = default;
                    Assert.That(s.TryGetSlot(TestItem.AddressA, 2, ref slot2), Is.True);
                    Assert.That(slot2.AsReadOnlySpan.ToArray(), Is.EqualTo(new SlotValue(new byte[] { 0x99 }).AsReadOnlySpan.ToArray()));
                    Assert.That(s.TryGetSelfDestructFlag(TestItem.AddressA), Is.False, "Destruct flag must be present and value must be `false` (destructed)");
                }))
                .SetName("Merge_SelfDestruct_ClearsOlderStorage");
        }

        // Newer true flag doesn't overwrite older false (destructed) — TryAdd semantics.
        {
            SnapshotContent c0 = new();
            c0.SelfDestructedStorageAddresses[TestItem.AddressA] = false;
            SnapshotContent c1 = new();
            c1.SelfDestructedStorageAddresses[TestItem.AddressA] = true;
            yield return new TestCaseData(
                (object)new[] { c0, c1 },
                (Action<PersistedSnapshot>)(s =>
                {
                    Assert.That(s.TryGetSelfDestructFlag(TestItem.AddressA), Is.False,
                        "Older `false` (destructed) flag must win over newer `true` (new-account) flag");
                }))
                .SetName("Merge_SelfDestruct_TryAddSemantics");
        }

        // Storage trie nodes survive self-destruct (only storage *slot* data is cleared).
        {
            Hash256 addrHash = Keccak.Compute(TestItem.AddressA.Bytes);
            TreePath storagePath = new(Keccak.Compute("storage_path"), 4);
            SnapshotContent c0 = new();
            c0.StorageNodes[(addrHash, storagePath)] = new TrieNode(NodeType.Leaf, [0xC1, 0x80]);
            SnapshotContent c1 = new();
            c1.SelfDestructedStorageAddresses[TestItem.AddressA] = false;
            yield return new TestCaseData(
                (object)new[] { c0, c1 },
                (Action<PersistedSnapshot>)(s =>
                {
                    Assert.That(s.TryLoadStorageNodeRlp(addrHash.ValueHash256, storagePath, out byte[]? rlp), Is.True,
                        "Storage trie node must survive self-destruct of the account");
                    Assert.That(rlp, Is.EqualTo(new byte[] { 0xC1, 0x80 }));
                }))
                .SetName("Merge_SelfDestruct_StorageNodesKept");
        }
    }

    [TestCaseSource(nameof(MergeValidationTestCases))]
    public void MergeSnapshots_ValidatesCorrectly(SnapshotContent[] contents, Action<PersistedSnapshot> assertCompacted)
    {
        string testDir = Path.Combine(Path.GetTempPath(), $"nethermind_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(testDir);
        try
        {
            using ArenaManager smallArena = new(Path.Combine(testDir, "arenas", "base"), 0, maxArenaSize: 64 * 1024);
            using BlobArenaManager smallBlobs = new(Path.Combine(testDir, "blobs", "small"), 1024 * 1024, PersistedSnapshotTier.Persisted);
            using PersistedSnapshotRepository repo = new(smallArena, smallBlobs, new MemDb(), new FlatDbConfig(), new PersistedSnapshotBloomFilterManager(), LimboLogs.Instance);
            repo.LoadFromCatalog();

            // minCompactSize == maxCompactSize == 2 — only a size-2 compaction is attempted, so
            // exactly two consecutive base snapshots are merged into one compacted snapshot.
            IFlatDbConfig config = new FlatDbConfig { CompactSize = 1, MinCompactSize = 2 };
            PersistedSnapshotCompactor compactor = new(
                repo, smallArena, config, ScheduleHelper.CreateWithOffset(config, 0),
                Nethermind.Logging.LimboLogs.Instance, new PersistedSnapshotBloomFilterManager(),
                minCompactSize: 2,
                maxCompactSize: 2);

            StateId[] states = new StateId[contents.Length + 1];
            states[0] = new StateId(0, Keccak.EmptyTreeHash);
            for (int i = 0; i < contents.Length; i++)
            {
                states[i + 1] = new StateId(i + 1, Keccak.Compute($"{i + 1}"));
                repo.ConvertSnapshotToPersistedSnapshot(
                    new Snapshot(states[i], states[i + 1], contents[i], _pool, ResourcePool.Usage.MainBlockProcessing)).Dispose();
            }

            compactor.DoCompactSnapshot(states[contents.Length]);

            Assert.That(repo.TryLeaseCompactedSnapshotTo(states[contents.Length], out PersistedSnapshot? compacted), Is.True,
                "Expected a compacted snapshot to exist after DoCompactSnapshot");
            using (compacted)
            {
                assertCompacted(compacted!);
            }
        }
        finally
        {
            if (Directory.Exists(testDir))
                Directory.Delete(testDir, recursive: true);
        }
    }

    // Config: compactSize=1 (PersistenceManager boundary), minCompactSize=2, maxCompactSize=8.
    // blockNumber=8 → 8 & -8 = 8, so the compaction window is [0, 8].
    //
    // presentBlocks: which block-slots are populated (snapshot From=states[b-1], To=states[b]).
    // The window need not be fully populated — whatever contiguous chain of ≥2 snapshots
    // assembles back from block 8 is compacted into a single snapshot.
    // expectCompacted=false means no compaction expected.
    private static IEnumerable<TestCaseData> PartialWindowCompactionCases()
    {
        // Full 8-block range present: compacts the whole window. Linked s0→s8.
        yield return new TestCaseData(new[] { 1, 2, 3, 4, 5, 6, 7, 8 }, true, 0L, 8L)
            .SetName("PartialWindow_FullRange_Compacts0To8");

        // Blocks 3–8 present: the chain reaches back to s2, a non-power-of-2 boundary.
        // The old power-of-2 step-down would have compacted only [4,8]; now the whole
        // assembled chain [2,8] is compacted instead.
        yield return new TestCaseData(new[] { 3, 4, 5, 6, 7, 8 }, true, 2L, 8L)
            .SetName("PartialWindow_NonPowerOfTwoStart_Compacts2To8");

        // Only blocks 5–8 present: chain reaches back to s4. Compacts [4,8].
        yield return new TestCaseData(new[] { 5, 6, 7, 8 }, true, 4L, 8L)
            .SetName("PartialWindow_Half_Compacts4To8");

        // Only blocks 7–8 present: chain reaches back to s6. Compacts [6,8].
        yield return new TestCaseData(new[] { 7, 8 }, true, 6L, 8L)
            .SetName("PartialWindow_Quarter_Compacts6To8");

        // Only 1 block present: no pair available, no compaction.
        yield return new TestCaseData(new[] { 8 }, false, 0L, 0L)
            .SetName("PartialWindow_NoRange_NoCompact");
    }

    [TestCaseSource(nameof(PartialWindowCompactionCases))]
    public void DoCompactSnapshot_CompactsPartialWindow(
        int[] presentBlocks, bool expectCompacted, long expectedFromBlock, long expectedToBlock)
    {
        string testDir = Path.Combine(Path.GetTempPath(), $"nethermind_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(testDir);
        try
        {
            using ArenaManager smallArena = new(Path.Combine(testDir, "arenas", "base"), 0, maxArenaSize: 64 * 1024);
            using BlobArenaManager smallBlobs = new(Path.Combine(testDir, "blobs", "small"), 1024 * 1024, PersistedSnapshotTier.Persisted);
            using PersistedSnapshotRepository repo = new(smallArena, smallBlobs, new MemDb(), new FlatDbConfig(), new PersistedSnapshotBloomFilterManager(), LimboLogs.Instance);
            repo.LoadFromCatalog();

            // CompactSize=1 makes every block a boundary; block 8 → window [0, 8].
            IFlatDbConfig config = new FlatDbConfig { CompactSize = 1, MinCompactSize = 2, PersistedSnapshotMaxCompactSize = 8 };
            PersistedSnapshotCompactor compactor = new(
                repo, smallArena, config,
                ScheduleHelper.CreateWithOffset(config, 0),
                Nethermind.Logging.LimboLogs.Instance, new PersistedSnapshotBloomFilterManager(),
                minCompactSize: config.CompactSize * 2,
                maxCompactSize: config.PersistedSnapshotMaxCompactSize);

            StateId[] states = new StateId[9];
            states[0] = new StateId(0, Keccak.EmptyTreeHash);
            for (int i = 1; i <= 8; i++)
                states[i] = new StateId(i, Keccak.Compute($"{i}"));

            foreach (int block in presentBlocks)
            {
                SnapshotContent content = new();
                content.Accounts[TestItem.Addresses[block - 1]] = Build.An.Account.WithBalance((ulong)block * 100).TestObject;
                repo.ConvertSnapshotToPersistedSnapshot(new Snapshot(states[block - 1], states[block], content, _pool, ResourcePool.Usage.MainBlockProcessing)).Dispose();
            }

            compactor.DoCompactSnapshot(states[8]);

            if (!expectCompacted)
            {
                Assert.That(repo.TryLeaseCompactedSnapshotTo(states[8], out PersistedSnapshot? none), Is.False,
                    "Expected no compacted snapshot");
                _ = none;
            }
            else
            {
                Assert.That(repo.TryLeaseCompactedSnapshotTo(states[8], out PersistedSnapshot? compacted), Is.True,
                    "Expected a compacted snapshot");
                Assert.That(compacted!.From.BlockNumber, Is.EqualTo(expectedFromBlock));
                Assert.That(compacted.To.BlockNumber, Is.EqualTo(expectedToBlock));
                compacted.Dispose();
            }
        }
        finally
        {
            if (Directory.Exists(testDir))
                Directory.Delete(testDir, recursive: true);
        }
    }

    /// <summary>
    /// After compaction, <see cref="PersistedSnapshot.TryLoadStateNodeRlp"/> /
    /// <see cref="PersistedSnapshot.TryLoadStorageNodeRlp"/> must dereference the merged
    /// snapshot's per-key <c>NodeRef</c>s through the union of referenced blob arenas
    /// and yield the newest-writer RLP for overlapping paths, the only-writer RLP for
    /// non-overlapping paths.
    /// </summary>
    [Test]
    public void CompactedSnapshot_TrieNodeResolution_NewerOverridesOlder()
    {
        string testDir = Path.Combine(Path.GetTempPath(), $"nethermind_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(testDir);
        try
        {
            using ArenaManager smallArena = new(Path.Combine(testDir, "arenas", "base"), 0, maxArenaSize: 64 * 1024);
            using BlobArenaManager smallBlobs = new(Path.Combine(testDir, "blobs", "small"), 1024 * 1024, PersistedSnapshotTier.Persisted);
            using PersistedSnapshotRepository repo = new(smallArena, smallBlobs, new MemDb(), new FlatDbConfig(), new PersistedSnapshotBloomFilterManager(), LimboLogs.Instance);
            repo.LoadFromCatalog();

            IFlatDbConfig config = new FlatDbConfig { CompactSize = 4, MinCompactSize = 2 };
            PersistedSnapshotCompactor compactor = new(
                repo, smallArena, config,
                ScheduleHelper.CreateWithOffset(config, 0),
                Nethermind.Logging.LimboLogs.Instance, new PersistedSnapshotBloomFilterManager(),
                minCompactSize: config.CompactSize * 2,
                maxCompactSize: config.PersistedSnapshotMaxCompactSize);

            TreePath sharedStatePath = new(Keccak.Compute("shared_state"), 4);
            TreePath onlyOldStatePath = new(Keccak.Compute("only_old_state"), 4);
            TreePath onlyNewStatePath = new(Keccak.Compute("only_new_state"), 4);
            Hash256 storageTrieAddr = Keccak.Compute("storage_trie_addr");
            TreePath sharedStoragePath = new(Keccak.Compute("shared_storage"), 6);

            byte[] oldStateRlp = [0xC1, 0x80];
            byte[] newStateRlp = [0xC2, 0x81, 0x42];
            byte[] onlyOldRlp = [0xC1, 0x33];
            byte[] onlyNewRlp = [0xC1, 0x55];
            byte[] oldStorageRlp = [0xC1, 0x80];
            byte[] newStorageRlp = [0xC2, 0x82, 0x99];

            StateId prev = new(0, Keccak.EmptyTreeHash);
            for (int i = 1; i <= 8; i++)
            {
                StateId next = new(i, Keccak.Compute($"{i}"));
                SnapshotContent c = new();
                if (i == 1)
                {
                    c.StateNodes[sharedStatePath] = new TrieNode(NodeType.Leaf, oldStateRlp);
                    c.StateNodes[onlyOldStatePath] = new TrieNode(NodeType.Leaf, onlyOldRlp);
                    c.StorageNodes[(storageTrieAddr, sharedStoragePath)] = new TrieNode(NodeType.Leaf, oldStorageRlp);
                }
                else if (i == 8)
                {
                    c.StateNodes[sharedStatePath] = new TrieNode(NodeType.Leaf, newStateRlp);
                    c.StateNodes[onlyNewStatePath] = new TrieNode(NodeType.Leaf, onlyNewRlp);
                    c.StorageNodes[(storageTrieAddr, sharedStoragePath)] = new TrieNode(NodeType.Leaf, newStorageRlp);
                }
                else
                {
                    c.Accounts[TestItem.Addresses[i - 1]] = Build.An.Account.WithBalance((UInt256)(i * 10)).TestObject;
                }
                repo.ConvertSnapshotToPersistedSnapshot(new Snapshot(prev, next, c, _pool, ResourcePool.Usage.MainBlockProcessing)).Dispose();
                prev = next;
            }

            compactor.DoCompactSnapshot(prev);

            Assert.That(repo.TryLeaseCompactedSnapshotTo(prev, out PersistedSnapshot? compacted), Is.True);
            using (compacted)
            {
                Assert.That(compacted!.TryLoadStateNodeRlp(sharedStatePath, out byte[]? sharedResult), Is.True);
                Assert.That(sharedResult, Is.EqualTo(newStateRlp),
                    "Overlapping state-node path must resolve to newest writer's RLP");

                Assert.That(compacted.TryLoadStateNodeRlp(onlyOldStatePath, out byte[]? oldOnly), Is.True);
                Assert.That(oldOnly, Is.EqualTo(onlyOldRlp),
                    "State node only in the oldest source must survive the merge with its original RLP");

                Assert.That(compacted.TryLoadStateNodeRlp(onlyNewStatePath, out byte[]? newOnly), Is.True);
                Assert.That(newOnly, Is.EqualTo(onlyNewRlp),
                    "State node only in the newest source must survive the merge with its original RLP");

                Assert.That(compacted.TryLoadStorageNodeRlp(storageTrieAddr.ValueHash256, sharedStoragePath, out byte[]? storageResult), Is.True);
                Assert.That(storageResult, Is.EqualTo(newStorageRlp),
                    "Overlapping storage-node path must resolve to newest writer's RLP");
            }
        }
        finally
        {
            if (Directory.Exists(testDir))
                Directory.Delete(testDir, recursive: true);
        }
    }

    /// <summary>
    /// Regression for the builder no-storage fast path in
    /// <c>PersistedSnapshotBuilder.WritePerAddressColumn</c>: when an address has no
    /// slots and no storage-trie nodes the per-address inner HSST is staged into a
    /// pooled buffer so its length is known up-front, and the outer leaf entry applies
    /// 4 KiB page-alignment padding. Drives many EOAs so writer positions sweep across
    /// page boundaries; every address must round-trip read intact and every self-destruct
    /// flag must survive the staging path. A mix of plain EOAs, EOA-with-SD and a few
    /// contracts (which take the streaming path) confirms both branches coexist.
    /// </summary>
    [TestCase(40)]
    [TestCase(120)]
    public void WritePerAddressColumn_NoStorageFastPath_RoundTripsEoaSnapshot(int accountCount)
    {
        string testDir = Path.Combine(Path.GetTempPath(), $"nethermind_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(testDir);
        try
        {
            using ArenaManager smallArena = new(Path.Combine(testDir, "arenas", "base"), 0, maxArenaSize: 256 * 1024);
            using BlobArenaManager smallBlobs = new(Path.Combine(testDir, "blobs", "small"), 4 * 1024 * 1024, PersistedSnapshotTier.Persisted);
            using PersistedSnapshotRepository repo = new(smallArena, smallBlobs, new MemDb(), new FlatDbConfig(), new PersistedSnapshotBloomFilterManager(), LimboLogs.Instance);
            repo.LoadFromCatalog();

            // Every 7th address gets storage (so the streaming path also fires) and the
            // routing decision flips per-address; every 5th address gets a self-destruct
            // flag (so the SD sub-tag is exercised on the staged DenseByteIndex).
            SnapshotContent c = new();
            for (int i = 0; i < accountCount; i++)
            {
                Address addr = TestItem.Addresses[i];
                c.Accounts[addr] = Build.An.Account.WithBalance((UInt256)(i + 1)).TestObject;
                if (i % 5 == 0)
                    c.SelfDestructedStorageAddresses[addr] = (i % 10 == 0);
                if (i % 7 == 0)
                    c.Storages[(addr, 1)] = new SlotValue(new byte[] { (byte)(i & 0xFF) });
            }

            StateId s0 = new(0, Keccak.EmptyTreeHash);
            StateId s1 = new(1, Keccak.Compute("p1"));
            repo.ConvertSnapshotToPersistedSnapshot(new Snapshot(s0, s1, c, _pool, ResourcePool.Usage.MainBlockProcessing)).Dispose();

            Assert.That(repo.TryLeaseSnapshotTo(s1, out PersistedSnapshot? built), Is.True);
            using (built)
            {
                Assert.Multiple(() =>
                {
                    for (int i = 0; i < accountCount; i++)
                    {
                        Address addr = TestItem.Addresses[i];
                        Assert.That(built!.TryGetAccount(addr, out Account? a), Is.True,
                            $"Account {i} ({(i % 7 == 0 ? "with-storage" : "no-storage")}) must survive WritePerAddressColumn");
                        Assert.That(a!.Balance, Is.EqualTo((UInt256)(i + 1)),
                            $"Account {i} balance mismatch — pad bytes leaked into the value range");
                        if (i % 5 == 0)
                        {
                            Assert.That(built.TryGetSelfDestructFlag(addr), Is.EqualTo((bool?)(i % 10 == 0)),
                                $"Self-destruct flag for account {i} must survive the staged DenseByteIndex path");
                        }
                        if (i % 7 == 0)
                        {
                            SlotValue slot = default;
                            Assert.That(built.TryGetSlot(addr, 1, ref slot), Is.True,
                                $"Slot for storage-bearing account {i} must come back from the streaming path");
                            SlotValue expected = new(new byte[] { (byte)(i & 0xFF) });
                            Assert.That(slot.AsReadOnlySpan.ToArray(), Is.EqualTo(expected.AsReadOnlySpan.ToArray()));
                        }
                    }
                });
            }
        }
        finally
        {
            if (Directory.Exists(testDir))
                Directory.Delete(testDir, recursive: true);
        }
    }

    /// <summary>
    /// Regression for the merger no-storage fast path in
    /// <c>PersistedSnapshotMerger.NWayMergePerAddressColumn</c>: two snapshots covering
    /// the SAME set of EOAs collide on every address (<c>matchCount &gt; 1</c>) without any
    /// source contributing slots or storage-trie nodes, so the staged-and-padded helper
    /// runs for every cursor address. Newest-wins on Account / first-non-empty on Address
    /// preimage / TryAdd on SD must all hold after the staged DenseByteIndex round-trips.
    /// </summary>
    [TestCase(40)]
    [TestCase(120)]
    public void Compact_MultiSourceMerge_NoStorageFastPath_RoundTrips(int accountCount)
    {
        string testDir = Path.Combine(Path.GetTempPath(), $"nethermind_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(testDir);
        try
        {
            using ArenaManager smallArena = new(Path.Combine(testDir, "arenas", "base"), 0, maxArenaSize: 256 * 1024);
            using BlobArenaManager smallBlobs = new(Path.Combine(testDir, "blobs", "small"), 4 * 1024 * 1024, PersistedSnapshotTier.Persisted);
            using PersistedSnapshotRepository repo = new(smallArena, smallBlobs, new MemDb(), new FlatDbConfig(), new PersistedSnapshotBloomFilterManager(), LimboLogs.Instance);
            repo.LoadFromCatalog();

            IFlatDbConfig config = new FlatDbConfig { CompactSize = 1, MinCompactSize = 2 };
            PersistedSnapshotCompactor compactor = new(
                repo, smallArena, config, ScheduleHelper.CreateWithOffset(config, 0),
                Nethermind.Logging.LimboLogs.Instance, new PersistedSnapshotBloomFilterManager(),
                minCompactSize: 2, maxCompactSize: 2);

            // Both sources touch every address with a different balance — collision on
            // every cursor address forces matchCount==2, and the absence of slots /
            // storage-trie nodes in either source flips the no-storage routing on.
            SnapshotContent c0 = new();
            SnapshotContent c1 = new();
            for (int i = 0; i < accountCount; i++)
            {
                Address addr = TestItem.Addresses[i];
                c0.Accounts[addr] = Build.An.Account.WithBalance((UInt256)(i + 1)).TestObject;
                c1.Accounts[addr] = Build.An.Account.WithBalance((UInt256)((i + 1) * 1000)).TestObject;
                // Every 5th address: set the destruct flag only in c0 (older). TryAdd
                // semantics must preserve it through the merge with c1 (which doesn't set
                // it), and the staged DenseByteIndex must emit it as sub-tag 0x03.
                if (i % 5 == 0)
                    c0.SelfDestructedStorageAddresses[addr] = false;
            }

            StateId s0 = new(0, Keccak.EmptyTreeHash);
            StateId s1 = new(1, Keccak.Compute("p1"));
            StateId s2 = new(2, Keccak.Compute("p2"));
            repo.ConvertSnapshotToPersistedSnapshot(new Snapshot(s0, s1, c0, _pool, ResourcePool.Usage.MainBlockProcessing)).Dispose();
            repo.ConvertSnapshotToPersistedSnapshot(new Snapshot(s1, s2, c1, _pool, ResourcePool.Usage.MainBlockProcessing)).Dispose();

            compactor.DoCompactSnapshot(s2);

            Assert.That(repo.TryLeaseCompactedSnapshotTo(s2, out PersistedSnapshot? compacted), Is.True);
            using (compacted)
            {
                Assert.Multiple(() =>
                {
                    for (int i = 0; i < accountCount; i++)
                    {
                        Address addr = TestItem.Addresses[i];
                        Assert.That(compacted!.TryGetAccount(addr, out Account? a), Is.True,
                            $"Account {i} must survive the staged multi-source merge");
                        Assert.That(a!.Balance, Is.EqualTo((UInt256)((i + 1) * 1000)),
                            $"Account {i}: newest balance (c1) must win — pad bytes must not leak into the value range");
                        if (i % 5 == 0)
                        {
                            Assert.That(compacted.TryGetSelfDestructFlag(addr), Is.False,
                                $"Self-destruct flag for account {i} must survive the staged DenseByteIndex merge");
                        }
                    }
                });
            }
        }
        finally
        {
            if (Directory.Exists(testDir))
                Directory.Delete(testDir, recursive: true);
        }
    }

    /// <summary>
    /// Regression for the offset-vs-block-number mismatch in
    /// <c>DoCompactSnapshot</c>'s <c>startingBlockNumber</c>. The alignment value comes
    /// from the offset-shifted schedule but the start-of-window was computed in raw
    /// block-number space — the previous
    /// <c>startingBlockNumber = ((blockNumber - 1) / alignment) * alignment</c> formula
    /// only matched the trigger's actual window when <c>offset == 0</c>. With a non-zero
    /// offset it produced a span of <c>(blockNumber mod alignment)</c> instead of
    /// <c>alignment</c>.
    ///
    /// Test geometry: offset=3, CompactSize=64, maxCompactSize=32. At block 45,
    /// <c>(45 + 3) &amp; -(45 + 3) = 48 &amp; -48 = 16</c>, so alignment=16 fires.
    /// Window must be <c>(29, 45]</c> (span 16), not the buggy <c>(32, 45]</c> (span 13).
    /// </summary>
    [Test]
    public void DoCompactSnapshot_WithNonZeroScheduleOffset_StartingBlockSpansFullAlignment()
    {
        string testDir = Path.Combine(Path.GetTempPath(), $"nethermind_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(testDir);
        try
        {
            using ArenaManager smallArena = new(Path.Combine(testDir, "arenas", "base"), 0, maxArenaSize: 256 * 1024);
            using BlobArenaManager smallBlobs = new(Path.Combine(testDir, "blobs", "small"), 4 * 1024 * 1024, PersistedSnapshotTier.Persisted);
            using PersistedSnapshotRepository repo = new(smallArena, smallBlobs, new MemDb(), new FlatDbConfig(), new PersistedSnapshotBloomFilterManager(), LimboLogs.Instance);
            repo.LoadFromCatalog();

            IFlatDbConfig config = new FlatDbConfig { CompactSize = 64, MinCompactSize = 2 };
            PersistedSnapshotCompactor compactor = new(
                repo, smallArena, config,
                ScheduleHelper.CreateWithOffset(config, 3),
                Nethermind.Logging.LimboLogs.Instance, new PersistedSnapshotBloomFilterManager(),
                minCompactSize: 2,
                maxCompactSize: 32);

            // 45 base snapshots, blocks 1..45. No intermediate compactions so
            // AssembleSnapshotsForCompaction sees only bases.
            StateId prev = new(0, Keccak.EmptyTreeHash);
            StateId tip = prev;
            for (int i = 1; i <= 45; i++)
            {
                StateId next = new(i, Keccak.Compute($"s{i}"));
                SnapshotContent c = new();
                c.Accounts[TestItem.AddressA] = Build.An.Account.WithBalance((UInt256)i).TestObject;
                repo.ConvertSnapshotToPersistedSnapshot(new Snapshot(prev, next, c, _pool, ResourcePool.Usage.MainBlockProcessing)).Dispose();
                prev = next;
                if (i == 45) tip = next;
            }

            // At block 45 with offset=3, alignment=16. Window must be (29, 45].
            compactor.DoCompactSnapshot(tip);

            Assert.That(repo.TryLeaseCompactedSnapshotTo(tip, out PersistedSnapshot? compacted), Is.True);
            try
            {
                Assert.That(compacted!.From.BlockNumber, Is.EqualTo(29),
                    "startingBlockNumber must be (blockNumber - alignment) — the left edge of the window the offset-shifted alignment trigger selects");
                Assert.That(compacted.To.BlockNumber, Is.EqualTo(45));
                Assert.That(compacted.To.BlockNumber - compacted.From.BlockNumber, Is.EqualTo(16),
                    "compacted span must equal alignment, not (blockNumber mod alignment)");
            }
            finally { compacted!.Dispose(); }
        }
        finally
        {
            if (Directory.Exists(testDir))
                Directory.Delete(testDir, recursive: true);
        }
    }
}
