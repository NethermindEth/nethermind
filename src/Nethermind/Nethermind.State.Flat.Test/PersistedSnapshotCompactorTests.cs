// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.Db;
using Nethermind.State.Flat.Hsst;
using Nethermind.State.Flat.PersistedSnapshots;
using Nethermind.State.Flat.Storage;
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

    private PersistedSnapshot CreatePersistedSnapshot(int id, StateId from, StateId to, PersistedSnapshotType type, byte[] data,
        PersistedSnapshot[]? referencedSnapshots = null)
    {
        using ArenaWriter writer = _memArena.CreateWriter(data.Length, ArenaReservationTags.Test);
        Span<byte> span = writer.GetWriter().GetSpan(data.Length);
        data.CopyTo(span);
        writer.GetWriter().Advance(data.Length);
        (_, ArenaReservation reservation) = writer.Complete();
        return new PersistedSnapshot(id, from, to, reservation, new Dictionary<ushort, BlobArenaFile>());
    }

    [Test]
    public void TryCompactPersistedSnapshots_MergesMultipleBaseSnapshots()
    {
        string testDir = Path.Combine(Path.GetTempPath(), $"nethermind_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(testDir);
        try
        {
            using ArenaManager smallArena = new(Path.Combine(testDir, "arenas", "base"), 0, maxArenaSize: 64 * 1024);
            using BlobArenaManager smallBlobs = new(Path.Combine(testDir, "blobs", "small"), 1024 * 1024, ArenaReservationTags.BlobSmall);
            using PersistedSnapshotRepository repo = new(smallArena, smallBlobs, new MemDb(), new FlatDbConfig(), new PersistedSnapshotBloomFilterManager());
            repo.LoadFromCatalog();

            // CompactSize=4, MinCompactSize=2. Use 8 blocks so compactSize = 8 & -8 = 8 > CompactSize=4, triggering compaction.
            // (compactSize == _compactSize is now skipped since persistable snapshots are produced by PersistenceManager)
            IFlatDbConfig config = new FlatDbConfig { CompactSize = 4, MinCompactSize = 2 };
            PersistedSnapshotCompactor compactor = new(
                repo, smallArena, config, Nethermind.Logging.LimboLogs.Instance, new PersistedSnapshotBloomFilterManager(),
                minCompactSize: config.CompactSize * 2,
                maxCompactSize: config.PersistedSnapshotMaxCompactSize,
                tierLabel: "large",
                reservationTag: ArenaReservationTags.BlobBackedLarge);

            StateId s0 = new(0, Keccak.EmptyTreeHash);
            StateId s1 = new(1, Keccak.Compute("1"));
            StateId s2 = new(2, Keccak.Compute("2"));
            StateId s3 = new(3, Keccak.Compute("3"));
            StateId s4 = new(4, Keccak.Compute("4"));
            StateId s5 = new(5, Keccak.Compute("5"));
            StateId s6 = new(6, Keccak.Compute("6"));
            StateId s7 = new(7, Keccak.Compute("7"));
            StateId s8 = new(8, Keccak.Compute("8"));

            // Create 8 consecutive base snapshots with different accounts
            SnapshotContent c1 = new();
            c1.Accounts[TestItem.AddressA] = Build.An.Account.WithBalance(100).TestObject;
            repo.ConvertSnapshotToPersistedSnapshot(new Snapshot(s0, s1, c1, _pool, ResourcePool.Usage.MainBlockProcessing));

            SnapshotContent c2 = new();
            c2.Accounts[TestItem.AddressB] = Build.An.Account.WithBalance(200).TestObject;
            repo.ConvertSnapshotToPersistedSnapshot(new Snapshot(s1, s2, c2, _pool, ResourcePool.Usage.MainBlockProcessing));

            SnapshotContent c3 = new();
            c3.Accounts[TestItem.AddressC] = Build.An.Account.WithBalance(300).TestObject;
            repo.ConvertSnapshotToPersistedSnapshot(new Snapshot(s2, s3, c3, _pool, ResourcePool.Usage.MainBlockProcessing));

            SnapshotContent c4 = new();
            c4.Accounts[TestItem.AddressD] = Build.An.Account.WithBalance(400).TestObject;
            repo.ConvertSnapshotToPersistedSnapshot(new Snapshot(s3, s4, c4, _pool, ResourcePool.Usage.MainBlockProcessing));

            SnapshotContent c5 = new();
            c5.Accounts[TestItem.AddressE] = Build.An.Account.WithBalance(500).TestObject;
            repo.ConvertSnapshotToPersistedSnapshot(new Snapshot(s4, s5, c5, _pool, ResourcePool.Usage.MainBlockProcessing));

            SnapshotContent c6 = new();
            c6.Accounts[TestItem.AddressF] = Build.An.Account.WithBalance(600).TestObject;
            repo.ConvertSnapshotToPersistedSnapshot(new Snapshot(s5, s6, c6, _pool, ResourcePool.Usage.MainBlockProcessing));

            SnapshotContent c7 = new();
            c7.Accounts[TestItem.Addresses[6]] = Build.An.Account.WithBalance(700).TestObject;
            repo.ConvertSnapshotToPersistedSnapshot(new Snapshot(s6, s7, c7, _pool, ResourcePool.Usage.MainBlockProcessing));

            SnapshotContent c8 = new();
            c8.Accounts[TestItem.Addresses[7]] = Build.An.Account.WithBalance(800).TestObject;
            repo.ConvertSnapshotToPersistedSnapshot(new Snapshot(s7, s8, c8, _pool, ResourcePool.Usage.MainBlockProcessing));

            compactor.DoCompactSnapshot(s8);

            // Compaction should have been triggered at block 8 (8 & -8 == 8 > CompactSize=4)
            // Verify compacted snapshot exists spanning 0→8 and contains all accounts
            Assert.That(repo.TryLeaseCompactedSnapshotTo(s8, out PersistedSnapshot? compacted), Is.True);
            Assert.That(compacted!.From, Is.EqualTo(s0));
            Assert.That(compacted.TryGetAccount(ValueKeccak.Compute(TestItem.AddressA.Bytes), out _), Is.True);
            Assert.That(compacted.TryGetAccount(ValueKeccak.Compute(TestItem.AddressB.Bytes), out _), Is.True);
            Assert.That(compacted.TryGetAccount(ValueKeccak.Compute(TestItem.AddressC.Bytes), out _), Is.True);
            Assert.That(compacted.TryGetAccount(ValueKeccak.Compute(TestItem.AddressD.Bytes), out _), Is.True);
            Assert.That(compacted.TryGetAccount(ValueKeccak.Compute(TestItem.AddressE.Bytes), out _), Is.True);
            Assert.That(compacted.TryGetAccount(ValueKeccak.Compute(TestItem.AddressF.Bytes), out _), Is.True);
            Assert.That(compacted.TryGetAccount(ValueKeccak.Compute(TestItem.Addresses[6].Bytes), out _), Is.True);
            Assert.That(compacted.TryGetAccount(ValueKeccak.Compute(TestItem.Addresses[7].Bytes), out _), Is.True);
            compacted.Dispose();
        }
        finally
        {
            if (Directory.Exists(testDir))
                Directory.Delete(testDir, recursive: true);
        }
    }

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
            using BlobArenaManager smallBlobs = new(Path.Combine(testDir, "blobs", "small"), 4 * 1024 * 1024, ArenaReservationTags.BlobSmall);
            using PersistedSnapshotRepository repo = new(smallArena, smallBlobs, new MemDb(), new FlatDbConfig(), new PersistedSnapshotBloomFilterManager());
            repo.LoadFromCatalog();

            // CompactSize=4 → minCompactSize for the large-tier compactor is 8. n is a power of 2
            // in {8, 16, 32}, so n & -n == n covers the whole window and triggers a single merge.
            IFlatDbConfig config = new FlatDbConfig { CompactSize = 4, MinCompactSize = 2 };
            PersistedSnapshotCompactor compactor = new(
                repo, smallArena, config, Nethermind.Logging.LimboLogs.Instance, new PersistedSnapshotBloomFilterManager(),
                minCompactSize: config.CompactSize * 2,
                maxCompactSize: config.PersistedSnapshotMaxCompactSize,
                tierLabel: "large",
                reservationTag: ArenaReservationTags.BlobBackedLarge);

            StateId prev = new(0, Keccak.EmptyTreeHash);
            ValueHash256 hashA = ValueKeccak.Compute(TestItem.AddressA.Bytes);
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
                repo.ConvertSnapshotToPersistedSnapshot(new Snapshot(prev, next, c, _pool, ResourcePool.Usage.MainBlockProcessing));
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
                    Assert.That(compacted.TryGetAccount(ValueKeccak.Compute(TestItem.Addresses[i - 1].Bytes), out _), Is.True,
                        $"Account from block {i} missing");
                }

                // Overlapping account: newest balance wins.
                Assert.That(compacted.TryGetAccount(hashA, out Account? a), Is.True);
                Assert.That(a!.Balance, Is.EqualTo((UInt256)n), "Newest balance must win on the overlapping account");

                // Every per-block slot must survive (each block wrote a distinct slot index).
                for (int i = 1; i <= n; i++)
                {
                    SlotValue slot = default;
                    Assert.That(compacted.TryGetSlot(hashA, (UInt256)i, ref slot), Is.True,
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

    [Test]
    public void CompactPersistedSnapshots_WarmsAddressIndexInPageResidencyTracker()
    {
        string testDir = Path.Combine(Path.GetTempPath(), $"nethermind_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(testDir);
        try
        {
            // Disabled tracker on the base arena (we don't care about source-side residency);
            // a real, sized tracker on the compacted arena so we can observe what
            // WarmAddressIndex registers after AdviseDontNeed. Budget = 1024 OS pages so the
            // tracker materialises at the expected capacity regardless of system page size.
            long largeBudget = 1024L * Environment.SystemPageSize;
            using ArenaManager smallArena = new(Path.Combine(testDir, "arenas", "base"), pageCacheBytes: largeBudget, maxArenaSize: 64 * 1024);
            using BlobArenaManager smallBlobs = new(Path.Combine(testDir, "blobs", "small"), 1024 * 1024, ArenaReservationTags.BlobSmall);
            PageResidencyTracker largeTracker = smallArena.PageTracker;
            using PersistedSnapshotRepository repo = new(smallArena, smallBlobs, new MemDb(), new FlatDbConfig(), new PersistedSnapshotBloomFilterManager());
            repo.LoadFromCatalog();

            // Validation off so the post-compaction validate path doesn't itself populate the
            // tracker via reads. Then any non-zero tracker count after DoCompactSnapshot must
            // come from WarmAddressIndex.
            IFlatDbConfig config = new FlatDbConfig { CompactSize = 4, MinCompactSize = 2, ValidatePersistedSnapshot = false };
            PersistedSnapshotCompactor compactor = new(
                repo, smallArena, config, Nethermind.Logging.LimboLogs.Instance, new PersistedSnapshotBloomFilterManager(),
                minCompactSize: config.CompactSize * 2,
                maxCompactSize: config.PersistedSnapshotMaxCompactSize,
                tierLabel: "large",
                reservationTag: ArenaReservationTags.BlobBackedLarge);

            StateId prev = new(0, Keccak.EmptyTreeHash);
            for (int i = 1; i <= 8; i++)
            {
                StateId next = new(i, Keccak.Compute($"s{i}"));
                SnapshotContent c = new();
                c.Accounts[TestItem.Addresses[i - 1]] = Build.An.Account.WithBalance((UInt256)(i * 100)).TestObject;
                repo.ConvertSnapshotToPersistedSnapshot(new Snapshot(prev, next, c, _pool, ResourcePool.Usage.MainBlockProcessing));
                prev = next;
            }

            Assert.That(largeTracker.Count, Is.Zero);

            compactor.DoCompactSnapshot(prev);

            Assert.That(largeTracker.Count, Is.GreaterThan(0),
                "WarmAddressIndex should register column-0x01 BTree index pages after compaction.");

            Assert.That(repo.TryLeaseCompactedSnapshotTo(prev, out PersistedSnapshot? compacted), Is.True);
            compacted!.Dispose();
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
            using BlobArenaManager smallBlobs = new(Path.Combine(testDir, "blobs", "small"), 1024 * 1024, ArenaReservationTags.BlobSmall);
            using PersistedSnapshotRepository repo = new(smallArena, smallBlobs, new MemDb(), new FlatDbConfig(), new PersistedSnapshotBloomFilterManager());
            repo.LoadFromCatalog();

            IFlatDbConfig config = new FlatDbConfig { CompactSize = 4, MinCompactSize = 2 };
            PersistedSnapshotCompactor compactor = new(
                repo, smallArena, config, Nethermind.Logging.LimboLogs.Instance, new PersistedSnapshotBloomFilterManager(),
                minCompactSize: config.CompactSize * 2,
                maxCompactSize: config.PersistedSnapshotMaxCompactSize,
                tierLabel: "large",
                reservationTag: ArenaReservationTags.BlobBackedLarge);

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
                repo.ConvertSnapshotToPersistedSnapshot(new Snapshot(prev, states[i], c, _pool, ResourcePool.Usage.MainBlockProcessing));
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
                    Assert.That(s.TryGetAccount(ValueKeccak.Compute(TestItem.AddressA.Bytes), out Account? a), Is.True);
                    Assert.That(a!.Balance, Is.EqualTo((UInt256)200));
                }))
                .SetName("Merge_AccountOverride");
        }

        // Regression: advance-corrupts-minKey bug in NWayStreamingMerge (StateTopNodes).
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
                    ValueHash256 hashA = ValueKeccak.Compute(TestItem.AddressA.Bytes);

                    Assert.That(s.TryGetAccount(hashA, out Account? a), Is.True);
                    Assert.That(a!.Balance, Is.EqualTo((UInt256)200), "Account override");

                    SlotValue slot1 = default;
                    Assert.That(s.TryGetSlot(hashA, 1, ref slot1), Is.True, "Older-only slot must survive (no self-destruct on A)");
                    Assert.That(slot1.AsReadOnlySpan.ToArray(), Is.EqualTo(new SlotValue(new byte[] { 0x42 }).AsReadOnlySpan.ToArray()));

                    SlotValue slot2 = default;
                    Assert.That(s.TryGetSlot(hashA, 2, ref slot2), Is.True);
                    Assert.That(slot2.AsReadOnlySpan.ToArray(), Is.EqualTo(new SlotValue(new byte[] { 0x99 }).AsReadOnlySpan.ToArray()));

                    Assert.That(s.IsSelfDestructed(ValueKeccak.Compute(TestItem.AddressB.Bytes)), Is.True,
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
                    Assert.That(s.TryGetAccount(ValueKeccak.Compute(TestItem.AddressA.Bytes), out Account? a), Is.True);
                    Assert.That(a!.Balance, Is.EqualTo((UInt256)100));
                    Assert.That(s.TryGetAccount(ValueKeccak.Compute(TestItem.AddressB.Bytes), out Account? b), Is.True);
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
                    ValueHash256 hashA = ValueKeccak.Compute(TestItem.AddressA.Bytes);
                    SlotValue slot1 = default;
                    Assert.That(s.TryGetSlot(hashA, 1, ref slot1), Is.False, "Older slot must be cleared by newer destruct");
                    SlotValue slot2 = default;
                    Assert.That(s.TryGetSlot(hashA, 2, ref slot2), Is.True);
                    Assert.That(slot2.AsReadOnlySpan.ToArray(), Is.EqualTo(new SlotValue(new byte[] { 0x99 }).AsReadOnlySpan.ToArray()));
                    Assert.That(s.IsSelfDestructed(hashA), Is.True, "Destruct flag must be present");
                    Assert.That(s.TryGetSelfDestructFlag(hashA), Is.False, "Destruct flag value must be `false` (destructed)");
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
                    ValueHash256 hashA = ValueKeccak.Compute(TestItem.AddressA.Bytes);
                    Assert.That(s.IsSelfDestructed(hashA), Is.True);
                    Assert.That(s.TryGetSelfDestructFlag(hashA), Is.False,
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
            using BlobArenaManager smallBlobs = new(Path.Combine(testDir, "blobs", "small"), 1024 * 1024, ArenaReservationTags.BlobSmall);
            using PersistedSnapshotRepository repo = new(smallArena, smallBlobs, new MemDb(), new FlatDbConfig(), new PersistedSnapshotBloomFilterManager());
            repo.LoadFromCatalog();

            // minCompactSize == maxCompactSize == 2 — only a size-2 compaction is attempted, so
            // exactly two consecutive base snapshots are merged into one compacted snapshot.
            IFlatDbConfig config = new FlatDbConfig { CompactSize = 1, MinCompactSize = 2 };
            PersistedSnapshotCompactor compactor = new(
                repo, smallArena, config, Nethermind.Logging.LimboLogs.Instance, new PersistedSnapshotBloomFilterManager(),
                minCompactSize: 2,
                maxCompactSize: 2,
                tierLabel: "test",
                reservationTag: ArenaReservationTags.BlobBackedLarge);

            StateId[] states = new StateId[contents.Length + 1];
            states[0] = new StateId(0, Keccak.EmptyTreeHash);
            for (int i = 0; i < contents.Length; i++)
            {
                states[i + 1] = new StateId(i + 1, Keccak.Compute($"{i + 1}"));
                repo.ConvertSnapshotToPersistedSnapshot(
                    new Snapshot(states[i], states[i + 1], contents[i], _pool, ResourcePool.Usage.MainBlockProcessing));
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
    // blockNumber=8 → 8 & -8 = 8. Loop tries 8 → 4 → 2 (each > _compactSize=1).
    //
    // presentBlocks: which block-slots are populated (snapshot From=states[b-1], To=states[b]).
    // expectedFromBlock=0 means no compaction expected.
    private static IEnumerable<TestCaseData> FallbackCompactionCases()
    {
        // Full 8-block range present: compacts at 8. Linked s0→s8.
        yield return new TestCaseData(new[] { 1, 2, 3, 4, 5, 6, 7, 8 }, true, 0L, 8L)
            .SetName("Fallback_FullRange_CompactsAt8");

        // Only blocks 5–8 present: falls back to 4. Linked s4→s8.
        yield return new TestCaseData(new[] { 5, 6, 7, 8 }, true, 4L, 8L)
            .SetName("Fallback_Half_CompactsAt4");

        // Only blocks 7–8 present: falls back to 2. Linked s6→s8.
        yield return new TestCaseData(new[] { 7, 8 }, true, 6L, 8L)
            .SetName("Fallback_Quarter_CompactsAt2");

        // Only 1 block present: no pair available, no compaction.
        yield return new TestCaseData(new[] { 8 }, false, 0L, 0L)
            .SetName("Fallback_NoRange_NoCompact");
    }

    [TestCaseSource(nameof(FallbackCompactionCases))]
    public void DoCompactSnapshot_FallsBackToSmallerCompactSize(
        int[] presentBlocks, bool expectCompacted, long expectedFromBlock, long expectedToBlock)
    {
        string testDir = Path.Combine(Path.GetTempPath(), $"nethermind_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(testDir);
        try
        {
            using ArenaManager smallArena = new(Path.Combine(testDir, "arenas", "base"), 0, maxArenaSize: 64 * 1024);
            using BlobArenaManager smallBlobs = new(Path.Combine(testDir, "blobs", "small"), 1024 * 1024, ArenaReservationTags.BlobSmall);
            using PersistedSnapshotRepository repo = new(smallArena, smallBlobs, new MemDb(), new FlatDbConfig(), new PersistedSnapshotBloomFilterManager());
            repo.LoadFromCatalog();

            // compactSize=1 keeps the loop running for sizes 2, 4, 8 (all > 1).
            IFlatDbConfig config = new FlatDbConfig { CompactSize = 1, MinCompactSize = 2, PersistedSnapshotMaxCompactSize = 8 };
            PersistedSnapshotCompactor compactor = new(
                repo, smallArena, config, Nethermind.Logging.LimboLogs.Instance, new PersistedSnapshotBloomFilterManager(),
                minCompactSize: config.CompactSize * 2,
                maxCompactSize: config.PersistedSnapshotMaxCompactSize,
                tierLabel: "large",
                reservationTag: ArenaReservationTags.BlobBackedLarge);

            StateId[] states = new StateId[9];
            states[0] = new StateId(0, Keccak.EmptyTreeHash);
            for (int i = 1; i <= 8; i++)
                states[i] = new StateId(i, Keccak.Compute($"{i}"));

            foreach (int block in presentBlocks)
            {
                SnapshotContent content = new();
                content.Accounts[TestItem.Addresses[block - 1]] = Build.An.Account.WithBalance((ulong)block * 100).TestObject;
                repo.ConvertSnapshotToPersistedSnapshot(new Snapshot(states[block - 1], states[block], content, _pool, ResourcePool.Usage.MainBlockProcessing));
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
            using BlobArenaManager smallBlobs = new(Path.Combine(testDir, "blobs", "small"), 1024 * 1024, ArenaReservationTags.BlobSmall);
            using PersistedSnapshotRepository repo = new(smallArena, smallBlobs, new MemDb(), new FlatDbConfig(), new PersistedSnapshotBloomFilterManager());
            repo.LoadFromCatalog();

            IFlatDbConfig config = new FlatDbConfig { CompactSize = 4, MinCompactSize = 2 };
            PersistedSnapshotCompactor compactor = new(
                repo, smallArena, config, Nethermind.Logging.LimboLogs.Instance, new PersistedSnapshotBloomFilterManager(),
                minCompactSize: config.CompactSize * 2,
                maxCompactSize: config.PersistedSnapshotMaxCompactSize,
                tierLabel: "large",
                reservationTag: ArenaReservationTags.BlobBackedLarge);

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
                repo.ConvertSnapshotToPersistedSnapshot(new Snapshot(prev, next, c, _pool, ResourcePool.Usage.MainBlockProcessing));
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
}
