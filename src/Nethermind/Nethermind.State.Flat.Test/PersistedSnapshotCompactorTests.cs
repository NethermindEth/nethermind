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
using Nethermind.State.Flat.Io;
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
    private ArenaManager _memArena = null!;
    private string _memArenaDir = null!;

    [SetUp]
    public void SetUp()
    {
        _pool = new ResourcePool(new FlatDbConfig());
        _memArenaDir = Path.Combine(Path.GetTempPath(), $"nm-compactortest-arena-{Guid.NewGuid():N}");
        _memArena = TestFixtureHelpers.CreateArenaManager(_memArenaDir);
    }

    [TearDown]
    public void TearDown()
    {
        _memArena.Dispose();
        try { Directory.Delete(_memArenaDir, recursive: true); } catch { /* best-effort */ }
    }

    /// <summary>
    /// Regression for large-tier compactions where N approaches the typical
    /// <c>compactSize/CompactSize</c> ceiling (~32). Each source carries a unique account
    /// plus a shared overlapping account (AddressA) with a distinct slot per block, so the
    /// per-address sub-tag merge runs with <c>matchCount == N</c> on every iteration and
    /// the slot merge exercises the fused inline bloom path with N slot inputs. Failures
    /// here flag mis-cached keys, missed bound refresh after <c>MoveNext</c>, or
    /// destruct-barrier/slot-bound mismatches in <c>MergeEntries</c>.
    /// </summary>
    [TestCase(8)]
    [TestCase(16)]
    [TestCase(32)]
    public void TryCompactPersistedSnapshots_MergesNBaseSnapshots(int n)
    {
        // CompactSize=4. n is a power of 2 in {8, 16, 32}, so n & -n == n: block n's natural
        // window covers the whole (0, n] range and DoCompactSnapshot triggers a single merge.
        using FlatTestContainer tier = new(
            arenaFileSizeBytes: 256 * 1024,
            blobFileSizeBytes: 4 * 1024 * 1024,
            configure: b => b.AddSingleton<ICompactionSchedule>(ScheduleHelper.CreateWithOffset(new FlatDbConfig { CompactSize = 4 }, 0)));
        SnapshotRepository repo = tier.Repository;
        PersistedSnapshotCompactor compactor = tier.Compactor;

        StateId prev = new(0, Keccak.EmptyTreeHash);
        for (int i = 1; i <= n; i++)
        {
            StateId next = new((ulong)i, Keccak.Compute($"s{i}"));
            SnapshotContent c = new();
            c.Accounts[TestItem.Addresses[i - 1]] = Build.An.Account.WithBalance((UInt256)(i * 100)).TestObject;
            // Shared overlapping account: same AddressA every block, distinct balance and
            // a distinct slot — drives matchCount == N through MergeEntries,
            // and the slot merge sees N inputs with N unique slot keys.
            c.Accounts[TestItem.AddressA] = Build.An.Account.WithBalance((UInt256)i).TestObject;
            c.Storages[(TestItem.AddressA, (UInt256)i)] = new SlotValue(new byte[] { (byte)i });
            tier.ConvertToPersistedBase(new Snapshot(prev, next, c, _pool, ResourcePool.Usage.MainBlockProcessing)).Dispose();
            prev = next;
        }

        compactor.DoCompactSnapshot(prev);

        Assert.That(repo.TryLeasePersistedState(prev, SnapshotTier.PersistedLargeCompacted, out PersistedSnapshot? compacted), Is.True);
        try
        {
            Assert.That(compacted!.From.BlockNumber, Is.EqualTo(0));
            Assert.That(compacted.To.BlockNumber, Is.EqualTo(n));

            for (int i = 1; i <= n; i++)
            {
                Assert.That(compacted.TryGetAccount(TestItem.Addresses[i - 1], out _), Is.True,
                    $"Account from block {i} missing");
            }

            Assert.That(compacted.TryGetAccount(TestItem.AddressA, out Account? a), Is.True);
            Assert.That(a!.Balance, Is.EqualTo((UInt256)n), "Newest balance must win on the overlapping account");

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

    /// <summary>
    /// Regression for bloom completeness on a single matching source (matchCount==1), which
    /// routes through the value mergers' <c>MergeValues</c> like any other key. We pack
    /// AddressA into one source with slots plus storage-trie nodes at every depth tier (top /
    /// compact / fallback) and pair it with an unrelated address in the second source so that
    /// matchCount==1 for AddressA. The merge must still bloom-add the address key, every slot
    /// key, and all three storage-trie sub-tag node keys. The bloom manager is shared with the
    /// compactor so <c>bloomCapacity</c> is non-zero and the merger produces a real
    /// (non-AlwaysTrue) bloom we can probe.
    /// </summary>
    [Test]
    public void Compact_SingleSourceAddress_AddsAllSubTagBloomKeys()
    {
        using FlatTestContainer tier = new(
            arenaFileSizeBytes: 64 * 1024,
            configure: b => b.AddSingleton<ICompactionSchedule>(ScheduleHelper.CreateWithOffset(new FlatDbConfig { CompactSize = 1, PersistedSnapshotMaxCompactSize = 2 }, 0)));
        SnapshotRepository repo = tier.Repository;
        PersistedSnapshotCompactor compactor = tier.Compactor;

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

        // Different address in the second source so AddressA has matchCount==1 (single
        // matching source) while still having ≥ 2 sources to compact.
        SnapshotContent c1 = new();
        c1.Accounts[TestItem.AddressB] = Build.An.Account.WithBalance(200).TestObject;

        StateId s0 = new(0, Keccak.EmptyTreeHash);
        StateId s1 = new(1, Keccak.Compute("s1"));
        StateId s2 = new(2, Keccak.Compute("s2"));
        tier.ConvertToPersistedBase(new Snapshot(s0, s1, c0, _pool, ResourcePool.Usage.MainBlockProcessing)).Dispose();
        tier.ConvertToPersistedBase(new Snapshot(s1, s2, c1, _pool, ResourcePool.Usage.MainBlockProcessing)).Dispose();

        compactor.DoCompactSnapshot(s2);

        Assert.That(repo.TryLeasePersistedState(s2, SnapshotTier.PersistedLargeCompacted, out PersistedSnapshot? compacted), Is.True);
        using (compacted)
        {
            BloomFilter bloom = compacted!.Bloom;
            Assert.That(bloom.Count, Is.GreaterThan(0),
                "Compacted snapshot must have a real bloom — the merge populates it from both sources");
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

    /// <summary>
    /// Metadata invariants for the blob-arena layout: base snapshots carry no
    /// <c>noderefs</c> flag and a single <c>ref_ids</c> entry (their own blob arena id);
    /// the compacted snapshot carries the <c>noderefs</c> flag and a <c>ref_ids</c> set
    /// equal to the union of source base-snapshot blob arena ids.
    /// </summary>
    [Test]
    public void CompactedSnapshot_Metadata_NodeRefsFlagAndRefIdsUnion()
    {
        using FlatTestContainer tier = new(
            arenaFileSizeBytes: 64 * 1024,
            configure: b => b.AddSingleton<ICompactionSchedule>(ScheduleHelper.CreateWithOffset(new FlatDbConfig { CompactSize = 4 }, 0)));
        SnapshotRepository repo = tier.Repository;
        PersistedSnapshotCompactor compactor = tier.Compactor;

        StateId prev = new(0, Keccak.EmptyTreeHash);
        StateId[] states = new StateId[9];
        states[0] = prev;
        HashSet<ushort> baseRefIds = [];
        for (int i = 1; i <= 8; i++)
        {
            states[i] = new StateId((ulong)i, Keccak.Compute($"{i}"));
            SnapshotContent c = new();
            c.Accounts[TestItem.Addresses[i - 1]] = Build.An.Account.WithBalance((UInt256)(i * 100)).TestObject;
            c.StateNodes[new TreePath(Keccak.Compute($"path{i}"), 4)] = new TrieNode(NodeType.Leaf, [(byte)(0xC1), (byte)i]);
            tier.ConvertToPersistedBase(new Snapshot(prev, states[i], c, _pool, ResourcePool.Usage.MainBlockProcessing)).Dispose();
            prev = states[i];
        }

        for (int i = 1; i <= 8; i++)
        {
            Assert.That(repo.TryLeasePersistedState(states[i], SnapshotTier.PersistedBase, out PersistedSnapshot? baseSnap), Is.True);
            using (baseSnap)
            {
                using WholeReadSession session = baseSnap!.BeginWholeReadSession();
                WholeReadSessionReader reader = session.CreateReader();
                ushort[]? ids = TestFixtureHelpers.ReadRefIdsFromMetadata<WholeReadSessionReader, NoOpPin>(in reader);
                Assert.That(ids, Is.Not.Null.And.Length.EqualTo(1),
                    $"Base snapshot {i} must carry exactly one blob-arena ref_id");
                baseRefIds.Add(ids![0]);
            }
        }

        compactor.DoCompactSnapshot(states[8]);

        Assert.That(repo.TryLeasePersistedState(states[8], SnapshotTier.PersistedLargeCompacted, out PersistedSnapshot? compacted), Is.True);
        using (compacted)
        {
            using WholeReadSession session = compacted!.BeginWholeReadSession();
            WholeReadSessionReader reader = session.CreateReader();
            ushort[]? mergedIds = TestFixtureHelpers.ReadRefIdsFromMetadata<WholeReadSessionReader, NoOpPin>(in reader);
            Assert.That(mergedIds, Is.Not.Null);
            Assert.That(new HashSet<ushort>(mergedIds!), Is.EquivalentTo(baseRefIds),
                "Compacted ref_ids must equal the union of source base blob-arena ids");
        }
    }

    private static IEnumerable<TestCaseData> MergeValidationTestCases()
    {
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

        // Single-source per-sub-tag merge: the same addressHash is present in both
        // sources (matchCount==2 for the storage-trie column), but the Top (4-byte key)
        // and Fallback (33-byte key) sub-tags are present in only the older source while
        // Compact (8-byte key) overlaps. This drives MergeStorageSubTag with active==1 for
        // Top and Fallback across both inner key widths and active==2 for Compact.
        {
            Hash256 addrHash = Keccak.Compute(TestItem.AddressA.Bytes);
            TreePath topPath = new(Keccak.Compute("trie_top"), 4);          // StorageTopSubTag (4-byte key)
            TreePath compactPath = new(Keccak.Compute("trie_compact"), 10); // StorageCompactSubTag (8-byte key)
            TreePath fallbackPath = new(Keccak.Compute("trie_fb"), 20);     // StorageFallbackSubTag (33-byte key)
            SnapshotContent c0 = new();
            c0.StorageNodes[(addrHash, topPath)] = new TrieNode(NodeType.Leaf, [0xC1, 0x80]);
            c0.StorageNodes[(addrHash, compactPath)] = new TrieNode(NodeType.Leaf, [0xC1, 0x81]);
            c0.StorageNodes[(addrHash, fallbackPath)] = new TrieNode(NodeType.Leaf, [0xC1, 0x82]);
            SnapshotContent c1 = new();
            c1.StorageNodes[(addrHash, compactPath)] = new TrieNode(NodeType.Leaf, [0xC2, 0x80, 0x81]);
            yield return new TestCaseData(
                (object)new[] { c0, c1 },
                (Action<PersistedSnapshot>)(s =>
                {
                    Assert.That(s.TryLoadStorageNodeRlp(addrHash.ValueHash256, topPath, out byte[]? topRlp), Is.True);
                    Assert.That(topRlp, Is.EqualTo(new byte[] { 0xC1, 0x80 }), "Top sub-tag (active==1) must survive");
                    Assert.That(s.TryLoadStorageNodeRlp(addrHash.ValueHash256, compactPath, out byte[]? compactRlp), Is.True);
                    Assert.That(compactRlp, Is.EqualTo(new byte[] { 0xC2, 0x80, 0x81 }), "Compact sub-tag (active==2) — newer wins");
                    Assert.That(s.TryLoadStorageNodeRlp(addrHash.ValueHash256, fallbackPath, out byte[]? fallbackRlp), Is.True);
                    Assert.That(fallbackRlp, Is.EqualTo(new byte[] { 0xC1, 0x82 }), "Fallback sub-tag (active==1) must survive");
                }))
                .SetName("Merge_SingleSourceSubTag_AllTiers");
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

        // Single-source, no-slot verbatim fast path: A (account-only EOA) and C (account +
        // self-destruct flag) appear in only one source and carry no slots, so each is
        // byte-copied verbatim through the outer builder; B keeps the second source non-empty.
        {
            SnapshotContent c0 = new();
            c0.Accounts[TestItem.AddressA] = Build.An.Account.WithBalance(100).TestObject;
            c0.Accounts[TestItem.AddressC] = Build.An.Account.WithBalance(300).TestObject;
            c0.SelfDestructedStorageAddresses[TestItem.AddressC] = false;
            SnapshotContent c1 = new();
            c1.Accounts[TestItem.AddressB] = Build.An.Account.WithBalance(200).TestObject;
            yield return new TestCaseData(
                (object)new[] { c0, c1 },
                (Action<PersistedSnapshot>)(s =>
                {
                    Assert.That(s.TryGetAccount(TestItem.AddressA, out Account? a), Is.True);
                    Assert.That(a!.Balance, Is.EqualTo((UInt256)100), "Account-only EOA copied verbatim");
                    SlotValue slotA = default;
                    Assert.That(s.TryGetSlot(TestItem.AddressA, 1, ref slotA), Is.False, "EOA has no slots");

                    Assert.That(s.TryGetAccount(TestItem.AddressC, out Account? c), Is.True);
                    Assert.That(c!.Balance, Is.EqualTo((UInt256)300), "Account survives verbatim copy");
                    Assert.That(s.TryGetSelfDestructFlag(TestItem.AddressC), Is.False,
                        "Self-destruct flag survives verbatim copy alongside the account sub-tag");

                    Assert.That(s.TryGetAccount(TestItem.AddressB, out Account? b), Is.True);
                    Assert.That(b!.Balance, Is.EqualTo((UInt256)200));
                }))
                .SetName("Merge_SingleSource_NoSlot_Verbatim");
        }
    }

    [TestCaseSource(nameof(MergeValidationTestCases))]
    public void MergeSnapshots_ValidatesCorrectly(SnapshotContent[] contents, Action<PersistedSnapshot> assertCompacted)
    {
        // maxCompactSize == 2 — only a size-2 compaction is attempted, so
        // exactly two consecutive base snapshots are merged into one compacted snapshot.
        using FlatTestContainer tier = new(
            arenaFileSizeBytes: 64 * 1024,
            configure: b => b.AddSingleton<ICompactionSchedule>(ScheduleHelper.CreateWithOffset(new FlatDbConfig { CompactSize = 1, PersistedSnapshotMaxCompactSize = 2 }, 0)));
        SnapshotRepository repo = tier.Repository;
        PersistedSnapshotCompactor compactor = tier.Compactor;

        StateId[] states = new StateId[contents.Length + 1];
        states[0] = new StateId(0, Keccak.EmptyTreeHash);
        for (int i = 0; i < contents.Length; i++)
        {
            states[i + 1] = new StateId((ulong)(i + 1), Keccak.Compute($"{i + 1}"));
            tier.ConvertToPersistedBase(
                new Snapshot(states[i], states[i + 1], contents[i], _pool, ResourcePool.Usage.MainBlockProcessing)).Dispose();
        }

        compactor.DoCompactSnapshot(states[contents.Length]);

        Assert.That(repo.TryLeasePersistedState(states[contents.Length], SnapshotTier.PersistedLargeCompacted, out PersistedSnapshot? compacted), Is.True,
            "Expected a compacted snapshot to exist after DoCompactSnapshot");
        using (compacted)
        {
            assertCompacted(compacted!);
        }
    }

    // Config: compactSize=1 (PersistenceManager boundary), maxCompactSize=8.
    // blockNumber=8 → 8 & -8 = 8, so the compaction window is [0, 8].
    //
    // presentBlocks: which block-slots are populated (snapshot From=states[b-1], To=states[b]).
    // The window need not be fully populated — whatever contiguous chain of ≥2 snapshots
    // assembles back from block 8 is compacted into a single snapshot.
    // expectCompacted=false means no compaction expected.
    private static IEnumerable<TestCaseData> PartialWindowCompactionCases()
    {
        // Full 8-block range present: compacts the whole window. Linked s0→s8.
        yield return new TestCaseData(new[] { 1, 2, 3, 4, 5, 6, 7, 8 }, true, 0UL, 8UL)
            .SetName("PartialWindow_FullRange_Compacts0To8");

        // Blocks 3–8 present: the chain reaches back to s2, a non-power-of-2 boundary.
        // The old power-of-2 step-down would have compacted only [4,8]; now the whole
        // assembled chain [2,8] is compacted instead.
        yield return new TestCaseData(new[] { 3, 4, 5, 6, 7, 8 }, true, 2UL, 8UL)
            .SetName("PartialWindow_NonPowerOfTwoStart_Compacts2To8");

        // Only blocks 5–8 present: chain reaches back to s4. Compacts [4,8].
        yield return new TestCaseData(new[] { 5, 6, 7, 8 }, true, 4UL, 8UL)
            .SetName("PartialWindow_Half_Compacts4To8");

        // Only blocks 7–8 present: chain reaches back to s6. Compacts [6,8].
        yield return new TestCaseData(new[] { 7, 8 }, true, 6UL, 8UL)
            .SetName("PartialWindow_Quarter_Compacts6To8");

        // Only 1 block present: no pair available, no compaction.
        yield return new TestCaseData(new[] { 8 }, false, 0UL, 0UL)
            .SetName("PartialWindow_NoRange_NoCompact");
    }

    [TestCaseSource(nameof(PartialWindowCompactionCases))]
    public void DoCompactSnapshot_CompactsPartialWindow(
        int[] presentBlocks, bool expectCompacted, ulong expectedFromBlock, ulong expectedToBlock)
    {
        // CompactSize=1 makes every block a boundary; block 8 → window [0, 8].
        using FlatTestContainer tier = new(
            arenaFileSizeBytes: 64 * 1024,
            configure: b => b.AddSingleton<ICompactionSchedule>(ScheduleHelper.CreateWithOffset(new FlatDbConfig { CompactSize = 1, PersistedSnapshotMaxCompactSize = 8 }, 0)));
        SnapshotRepository repo = tier.Repository;
        PersistedSnapshotCompactor compactor = tier.Compactor;

        StateId[] states = new StateId[9];
        states[0] = new StateId(0, Keccak.EmptyTreeHash);
        for (int i = 1; i <= 8; i++)
            states[i] = new StateId((ulong)i, Keccak.Compute($"{i}"));

        foreach (int block in presentBlocks)
        {
            SnapshotContent content = new();
            content.Accounts[TestItem.Addresses[block - 1]] = Build.An.Account.WithBalance((ulong)block * 100).TestObject;
            tier.ConvertToPersistedBase(new Snapshot(states[block - 1], states[block], content, _pool, ResourcePool.Usage.MainBlockProcessing)).Dispose();
        }

        compactor.DoCompactSnapshot(states[8]);

        if (!expectCompacted)
        {
            Assert.That(repo.TryLeasePersistedState(states[8], SnapshotTier.PersistedLargeCompacted, out PersistedSnapshot? none), Is.False,
                "Expected no compacted snapshot");
            _ = none;
        }
        else
        {
            Assert.That(repo.TryLeasePersistedState(states[8], SnapshotTier.PersistedLargeCompacted, out PersistedSnapshot? compacted), Is.True,
                "Expected a compacted snapshot");
            Assert.That(compacted!.From.BlockNumber, Is.EqualTo(expectedFromBlock));
            Assert.That(compacted.To.BlockNumber, Is.EqualTo(expectedToBlock));
            compacted.Dispose();
        }
    }

    // A [0,8] large-compacted (To=8) survives until persistence passes block 8, so its From=0 sits
    // below any persistence point in (0, 8]. The widest-skip-first assemble walk would follow that
    // edge and drag block 16's compaction down to From=0. Clamping the window to the persistence
    // point makes the walk reject the below-P edge and assemble from P upward via the bases instead.
    private static IEnumerable<TestCaseData> ClampToPersistenceCases()
    {
        // P at genesis: no clamp, the walk follows the [0,8] large-compacted skip-pointer to From=0.
        yield return new TestCaseData(0UL, 0UL).SetName("ClampToPersistence_GenesisP_NoClamp_From0");
        // P inside the [0,8] span: the below-P edge is skipped, the walk wins at From=P via the bases.
        yield return new TestCaseData(4UL, 4UL).SetName("ClampToPersistence_PInsideSpan_ClampsFrom4");
        // P at the [0,8] To boundary: still clamped, never reaching the From=0 edge.
        yield return new TestCaseData(8UL, 8UL).SetName("ClampToPersistence_PAtBoundary_ClampsFrom8");
    }

    [TestCaseSource(nameof(ClampToPersistenceCases))]
    public void DoCompactSnapshot_ClampsWindowToPersistencePoint(ulong persistedBlock, ulong expectedFromBlock)
    {
        // CompactSize=1 makes every block a boundary; MaxCompactSize=16 so block 16's window is [0, 16].
        using FlatTestContainer tier = new(
            arenaFileSizeBytes: 256 * 1024,
            blobFileSizeBytes: 4 * 1024 * 1024,
            configure: b => b.AddSingleton<ICompactionSchedule>(
                ScheduleHelper.CreateWithOffset(new FlatDbConfig { CompactSize = 1, PersistedSnapshotMaxCompactSize = 16 }, 0)));
        SnapshotRepository repo = tier.Repository;
        PersistedSnapshotCompactor compactor = tier.Compactor;

        StateId[] states = new StateId[17];
        states[0] = new StateId(0, Keccak.EmptyTreeHash);
        for (int i = 1; i <= 16; i++)
            states[i] = new StateId((ulong)i, Keccak.Compute($"{i}"));

        // Build base snapshots [0..8], then the [0,8] large-compacted skip-pointer.
        for (int i = 1; i <= 8; i++)
            BuildBase(tier, states, i);
        compactor.DoCompactSnapshot(states[8], persistedBlockNumber: 0);
        Assert.That(repo.TryLeasePersistedState(states[8], SnapshotTier.PersistedLargeCompacted, out PersistedSnapshot? seed), Is.True,
            "precondition: the [0,8] large-compacted skip-pointer must exist");
        seed!.Dispose();

        // Build base snapshots [9..16] so narrower edges exist above the persistence point.
        for (int i = 9; i <= 16; i++)
            BuildBase(tier, states, i);

        // Compact block 16's [0,16] window, clamped to the persistence point.
        compactor.DoCompactSnapshot(states[16], persistedBlockNumber: persistedBlock);

        Assert.That(repo.TryLeasePersistedState(states[16], SnapshotTier.PersistedLargeCompacted, out PersistedSnapshot? compacted), Is.True,
            "Expected a large-compacted snapshot at block 16");
        using (compacted)
        {
            Assert.That(compacted!.To.BlockNumber, Is.EqualTo(16));
            Assert.That(compacted.From.BlockNumber, Is.EqualTo(expectedFromBlock),
                persistedBlock == 0
                    ? "Unclamped: the walk follows the [0,8] large-compacted edge down to From=0"
                    : "Clamped: the below-P [0,8] edge is rejected and the walk wins at From=P");
        }
    }

    private void BuildBase(FlatTestContainer tier, StateId[] states, int block)
    {
        SnapshotContent content = new();
        content.Accounts[TestItem.Addresses[block - 1]] = Build.An.Account.WithBalance((ulong)block * 100).TestObject;
        tier.ConvertToPersistedBase(new Snapshot(states[block - 1], states[block], content, _pool, ResourcePool.Usage.MainBlockProcessing)).Dispose();
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
        using FlatTestContainer tier = new(
            arenaFileSizeBytes: 64 * 1024,
            configure: b => b.AddSingleton<ICompactionSchedule>(ScheduleHelper.CreateWithOffset(new FlatDbConfig { CompactSize = 4 }, 0)));
        SnapshotRepository repo = tier.Repository;
        PersistedSnapshotCompactor compactor = tier.Compactor;

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
            StateId next = new((ulong)i, Keccak.Compute($"{i}"));
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
            tier.ConvertToPersistedBase(new Snapshot(prev, next, c, _pool, ResourcePool.Usage.MainBlockProcessing)).Dispose();
            prev = next;
        }

        compactor.DoCompactSnapshot(prev);

        Assert.That(repo.TryLeasePersistedState(prev, SnapshotTier.PersistedLargeCompacted, out PersistedSnapshot? compacted), Is.True);
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
        using FlatTestContainer tier = new(
            arenaFileSizeBytes: 256 * 1024,
            blobFileSizeBytes: 4 * 1024 * 1024,
            configure: b => b.AddSingleton<ICompactionSchedule>(ScheduleHelper.CreateWithOffset(new FlatDbConfig { CompactSize = 1, PersistedSnapshotMaxCompactSize = 2 }, 0)));
        SnapshotRepository repo = tier.Repository;
        PersistedSnapshotCompactor compactor = tier.Compactor;

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
        tier.ConvertToPersistedBase(new Snapshot(s0, s1, c0, _pool, ResourcePool.Usage.MainBlockProcessing)).Dispose();
        tier.ConvertToPersistedBase(new Snapshot(s1, s2, c1, _pool, ResourcePool.Usage.MainBlockProcessing)).Dispose();

        compactor.DoCompactSnapshot(s2);

        Assert.That(repo.TryLeasePersistedState(s2, SnapshotTier.PersistedLargeCompacted, out PersistedSnapshot? compacted), Is.True);
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
    /// Test geometry: offset=3, CompactSize=64, maxCompactSize=64. At block 45,
    /// <c>(45 + 3) &amp; -(45 + 3) = 48 &amp; -48 = 16</c>, so alignment=16 fires.
    /// Window must be <c>(29, 45]</c> (span 16), not the buggy <c>(32, 45]</c> (span 13).
    /// </summary>
    [Test]
    public void DoCompactSnapshot_WithNonZeroScheduleOffset_StartingBlockSpansFullAlignment()
    {
        using FlatTestContainer tier = new(
            arenaFileSizeBytes: 256 * 1024,
            blobFileSizeBytes: 4 * 1024 * 1024,
            configure: b => b.AddSingleton<ICompactionSchedule>(ScheduleHelper.CreateWithOffset(new FlatDbConfig { CompactSize = 64, PersistedSnapshotMaxCompactSize = 64 }, 3)));
        SnapshotRepository repo = tier.Repository;
        PersistedSnapshotCompactor compactor = tier.Compactor;

        // 45 base snapshots, blocks 1..45. No intermediate compactions so
        // AssemblePersistedSnapshotsForCompaction sees only bases.
        StateId prev = new(0, Keccak.EmptyTreeHash);
        StateId tip = prev;
        for (int i = 1; i <= 45; i++)
        {
            StateId next = new((ulong)i, Keccak.Compute($"s{i}"));
            SnapshotContent c = new();
            c.Accounts[TestItem.AddressA] = Build.An.Account.WithBalance((UInt256)i).TestObject;
            tier.ConvertToPersistedBase(new Snapshot(prev, next, c, _pool, ResourcePool.Usage.MainBlockProcessing)).Dispose();
            prev = next;
            if (i == 45) tip = next;
        }

        // At block 45 with offset=3, alignment=16. Window must be (29, 45].
        compactor.DoCompactSnapshot(tip);

        Assert.That(repo.TryLeasePersistedState(tip, SnapshotTier.PersistedSmallCompacted, out PersistedSnapshot? compacted), Is.True);
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

    private static FlatTestContainer NewTier(int compactSize) => new(
        arenaFileSizeBytes: 256 * 1024,
        blobFileSizeBytes: 4 * 1024 * 1024,
        configure: b => b.AddSingleton<ICompactionSchedule>(ScheduleHelper.CreateWithOffset(new FlatDbConfig { CompactSize = (ulong)compactSize }, 0)));

    [Test]
    public void DoCompactSnapshot_NoOp_WhenWindowSizeOneOrTooFewSnapshots()
    {
        using FlatTestContainer tier = NewTier(compactSize: 4);
        PersistedSnapshotCompactor compactor = tier.Compactor;

        // Block 1: natural window size is 1 → nothing to merge.
        compactor.DoCompactSnapshot(new StateId(1, Keccak.Compute("b1")));
        // Block 4: window size 4, but the empty repo has < 2 snapshots.
        compactor.DoCompactSnapshot(new StateId(4, Keccak.Compute("b4")));

        Assert.That(tier.Repository.PersistedSnapshotCount, Is.EqualTo(0), "no compaction should have run");
    }

    [Test]
    public void DoCompactCompactSized_NoOp_WhenNotBoundaryOrTooFewSnapshots()
    {
        using FlatTestContainer tier = NewTier(compactSize: 4);
        PersistedSnapshotCompactor compactor = tier.Compactor;

        compactor.DoCompactCompactSized(new StateId(3, Keccak.Compute("b3"))); // not a boundary
        compactor.DoCompactCompactSized(new StateId(4, Keccak.Compute("b4"))); // boundary, but empty repo

        Assert.That(tier.Repository.PersistedSnapshotCount, Is.EqualTo(0), "no CompactSized snapshot should have been produced");
    }

    [Test]
    public void DoCompactCompactSized_AtBoundary_ProducesCompactSizedSnapshot()
    {
        using FlatTestContainer tier = NewTier(compactSize: 4);
        SnapshotRepository repo = tier.Repository;
        PersistedSnapshotCompactor compactor = tier.Compactor;

        StateId prev = new(0, Keccak.EmptyTreeHash);
        StateId tip = prev;
        for (int i = 1; i <= 4; i++)
        {
            tip = new((ulong)i, Keccak.Compute($"p{i}"));
            SnapshotContent c = new();
            c.Accounts[TestItem.Addresses[i - 1]] = Build.An.Account.WithBalance((UInt256)(i * 10)).TestObject;
            tier.ConvertToPersistedBase(new Snapshot(prev, tip, c, _pool, ResourcePool.Usage.MainBlockProcessing)).Dispose();
            prev = tip;
        }

        compactor.DoCompactCompactSized(tip);

        Assert.That(repo.TryLeasePersistedState(tip, SnapshotTier.PersistedCompactSized, out PersistedSnapshot? compactSized), Is.True);
        try
        {
            Assert.That(compactSized!.From.BlockNumber, Is.EqualTo(0));
            Assert.That(compactSized.To.BlockNumber, Is.EqualTo(4));
            for (int i = 1; i <= 4; i++)
                Assert.That(compactSized.TryGetAccount(TestItem.Addresses[i - 1], out _), Is.True, $"account from block {i} missing");
        }
        finally { compactSized!.Dispose(); }
    }

    [Test]
    public void DoCompactSnapshot_AtBoundary_NoAddressColumn_WarmsGracefully()
    {
        using FlatTestContainer tier = NewTier(compactSize: 2);
        SnapshotRepository repo = tier.Repository;
        PersistedSnapshotCompactor compactor = tier.Compactor;

        StateId prev = new(0, Keccak.EmptyTreeHash);
        StateId tip = prev;
        for (int i = 1; i <= 2; i++)
        {
            tip = new((ulong)i, Keccak.Compute($"sn{i}"));
            SnapshotContent c = new();
            TreePath path = new(Keccak.Compute($"node{i}"), 4);
            c.StateNodes[path] = new TrieNode(NodeType.Leaf, [0xC2, 0x80, (byte)i]);
            tier.ConvertToPersistedBase(new Snapshot(prev, tip, c, _pool, ResourcePool.Usage.MainBlockProcessing)).Dispose();
            prev = tip;
        }

        compactor.DoCompactSnapshot(tip); // block 2 is a CompactSize=2 boundary → WarmAddressColumnIndex path

        Assert.That(repo.TryLeasePersistedState(tip, SnapshotTier.PersistedSmallCompacted, out PersistedSnapshot? compacted), Is.True);
        try
        {
            Assert.That(compacted!.To.BlockNumber, Is.EqualTo(2));
            TreePath probe = new(Keccak.Compute("node2"), 4);
            Assert.That(compacted.TryLoadStateNodeRlp(probe, out _), Is.True, "state node must survive the no-address-column compaction");
        }
        finally { compacted!.Dispose(); }
    }

    /// <summary>
    /// A sub-<c>CompactSize</c> intermediate merge lands in the <see cref="SnapshotTier.PersistedSmallCompacted"/>
    /// tier; a <c>&gt;CompactSize</c> large-boundary merge lands in <see cref="SnapshotTier.PersistedLargeCompacted"/>.
    /// Each tier resolves only from its own bucket — a lease for the other tier at the same <c>To</c> misses.
    /// </summary>
    [Test]
    public void DoCompactSnapshot_SplitsCompactedAndLargeCompactedByWindowWidth()
    {
        // CompactSize=4: block 2's window (0,2] spans 2 (< 4) → compacted; block 8's window (0,8] spans 8 (> 4) → large.
        using FlatTestContainer tier = NewTier(compactSize: 4);
        SnapshotRepository repo = tier.Repository;
        PersistedSnapshotCompactor compactor = tier.Compactor;

        StateId prev = new(0, Keccak.EmptyTreeHash);
        StateId[] states = new StateId[9];
        states[0] = prev;
        for (int i = 1; i <= 8; i++)
        {
            states[i] = new StateId((ulong)i, Keccak.Compute($"s{i}"));
            SnapshotContent c = new();
            c.Accounts[TestItem.Addresses[i - 1]] = Build.An.Account.WithBalance((UInt256)(i * 100)).TestObject;
            tier.ConvertToPersistedBase(new Snapshot(prev, states[i], c, _pool, ResourcePool.Usage.MainBlockProcessing)).Dispose();
            prev = states[i];
        }

        compactor.DoCompactSnapshot(states[2]); // sub-CompactSize intermediate
        compactor.DoCompactSnapshot(states[8]); // >CompactSize large-boundary merge

        Assert.Multiple(() =>
        {
            Assert.That(repo.TryLeasePersistedState(states[2], SnapshotTier.PersistedSmallCompacted, out PersistedSnapshot? compacted), Is.True,
                "sub-CompactSize window must be a PersistedSmallCompacted snapshot");
            using (compacted) Assert.That(compacted!.To.BlockNumber, Is.EqualTo(2));

            Assert.That(repo.TryLeasePersistedState(states[2], SnapshotTier.PersistedLargeCompacted, out _), Is.False,
                "PersistedSmallCompacted must not resolve from the large-compacted bucket");

            Assert.That(repo.TryLeasePersistedState(states[8], SnapshotTier.PersistedLargeCompacted, out PersistedSnapshot? large), Is.True,
                ">CompactSize window must be a PersistedLargeCompacted snapshot");
            using (large) Assert.That(large!.To.BlockNumber, Is.EqualTo(8));

            Assert.That(repo.TryLeasePersistedState(states[8], SnapshotTier.PersistedSmallCompacted, out _), Is.False,
                "PersistedLargeCompacted must not resolve from the compacted bucket");
        });
    }

    /// <summary>
    /// A sub-<c>CompactSize</c> intermediate takes a minimal single-cache-line bloom (<c>Capacity == 1</c>)
    /// rather than a capacity-sized one: it is read-cold and short-lived, so it skips the real bloom. The
    /// filter is still populated by the merge (<c>Count &gt; 0</c>), keeping its key count accurate for a
    /// wider merge that later consumes it, and it must still read back correctly.
    /// </summary>
    [Test]
    public void SubCompactSizeIntermediate_UsesTinyBloom()
    {
        // CompactSize=4: block 2's window (0,2] spans 2 (< 4) → sub-CompactSize intermediate. No large
        // boundary is compacted, so nothing shares over it.
        using FlatTestContainer tier = NewTier(compactSize: 4);
        SnapshotRepository repo = tier.Repository;
        PersistedSnapshotCompactor compactor = tier.Compactor;

        StateId prev = new(0, Keccak.EmptyTreeHash);
        StateId[] states = new StateId[3];
        states[0] = prev;
        for (int i = 1; i <= 2; i++)
        {
            states[i] = new StateId((ulong)i, Keccak.Compute($"s{i}"));
            SnapshotContent c = new();
            c.Accounts[TestItem.Addresses[i - 1]] = Build.An.Account.WithBalance((UInt256)(i * 100)).TestObject;
            tier.ConvertToPersistedBase(new Snapshot(prev, states[i], c, _pool, ResourcePool.Usage.MainBlockProcessing)).Dispose();
            prev = states[i];
        }

        compactor.DoCompactSnapshot(states[2]); // sub-CompactSize intermediate → tiny bloom

        Assert.That(repo.TryLeasePersistedState(states[2], SnapshotTier.PersistedSmallCompacted, out PersistedSnapshot? intermediate), Is.True);
        using (intermediate)
        {
            Assert.Multiple(() =>
            {
                // Minimal (capacity-1) bloom, but still populated by the merge (Count > 0), not the
                // Count==0 AlwaysTrue sentinel and not a capacity-sized filter.
                Assert.That(intermediate!.Bloom.Capacity, Is.EqualTo(1), "sub-CompactSize intermediate must take the tiny bloom");
                Assert.That(intermediate.Bloom.Count, Is.GreaterThan(0), "tiny bloom is still populated, not AlwaysTrue");
                Assert.That(intermediate.TryGetAccount(TestItem.Addresses[0], out Account? a1), Is.True);
                Assert.That(a1!.Balance, Is.EqualTo((UInt256)100));
                Assert.That(intermediate.TryGetAccount(TestItem.Addresses[1], out Account? a2), Is.True);
                Assert.That(a2!.Balance, Is.EqualTo((UInt256)200));
            });
        }
    }

    /// <summary>
    /// A <c>&gt;CompactSize</c> large-boundary merge adopts its own (superset) bloom across every persisted
    /// snapshot fully contained in its <c>(from, to]</c> window — base, sub-<c>CompactSize</c> intermediate
    /// and CompactSized alike. Each contained snapshot ends up reference-equal to the big merge's bloom (so
    /// its own bloom is freed) and still reads back correctly. Regression for bloom sharing.
    /// </summary>
    [Test]
    public void LargeBoundary_SharesBloomAcrossContainedSnapshots()
    {
        // CompactSize=4: block 8's window (0,8] spans 8 (> 4) → large boundary → shares its bloom.
        using FlatTestContainer tier = NewTier(compactSize: 4);
        SnapshotRepository repo = tier.Repository;
        PersistedSnapshotCompactor compactor = tier.Compactor;

        StateId prev = new(0, Keccak.EmptyTreeHash);
        StateId[] states = new StateId[9];
        states[0] = prev;
        for (int i = 1; i <= 8; i++)
        {
            states[i] = new StateId((ulong)i, Keccak.Compute($"s{i}"));
            SnapshotContent c = new();
            c.Accounts[TestItem.Addresses[i - 1]] = Build.An.Account.WithBalance((UInt256)(i * 100)).TestObject;
            tier.ConvertToPersistedBase(new Snapshot(prev, states[i], c, _pool, ResourcePool.Usage.MainBlockProcessing)).Dispose();
            prev = states[i];
        }

        compactor.DoCompactSnapshot(states[2]);     // sub-CompactSize intermediate (small compacted)
        compactor.DoCompactCompactSized(states[4]); // CompactSize boundary → CompactSized
        compactor.DoCompactSnapshot(states[8]);     // large boundary → shares its bloom across (0,8]

        Assert.That(repo.TryLeasePersistedState(states[8], SnapshotTier.PersistedLargeCompacted, out PersistedSnapshot? big), Is.True);
        using (big)
        {
            BloomFilter shared = big!.Bloom;
            Assert.That(shared.Count, Is.GreaterThan(0), "the large merge keeps a real, populated bloom");

            // The sub-CompactSize intermediate and the CompactSized both adopt the shared bloom.
            AssertShares(repo, states[2], SnapshotTier.PersistedSmallCompacted, shared);
            AssertShares(repo, states[4], SnapshotTier.PersistedCompactSized, shared);

            // Every contained base snapshot adopts the shared bloom and still resolves its account.
            for (int i = 1; i <= 8; i++)
            {
                Assert.That(repo.TryLeasePersistedState(states[i], SnapshotTier.PersistedBase, out PersistedSnapshot? baseSnap), Is.True);
                using (baseSnap)
                {
                    Assert.That(ReferenceEquals(baseSnap!.Bloom, shared), Is.True, $"base {i} should share the big merge's bloom");
                    Assert.That(baseSnap.TryGetAccount(TestItem.Addresses[i - 1], out Account? a), Is.True, $"account from block {i} must still resolve");
                    Assert.That(a!.Balance, Is.EqualTo((UInt256)(i * 100)));
                }
            }
        }

        static void AssertShares(SnapshotRepository repo, StateId at, SnapshotTier tier, BloomFilter shared)
        {
            Assert.That(repo.TryLeasePersistedState(at, tier, out PersistedSnapshot? s), Is.True, $"{tier} at {at.BlockNumber} must exist");
            using (s)
                Assert.That(ReferenceEquals(s!.Bloom, shared), Is.True, $"{tier} at {at.BlockNumber} should share the big merge's bloom");
        }
    }

    /// <summary>
    /// A snapshot extending below the big merge's <c>from</c> (its keys are not a subset of the merge's
    /// window) must NOT adopt the merge's bloom — sharing it would yield false negatives. Builds a [0,8]
    /// large skip-pointer, then a [4,16] big merge clamped to persistence block 4, and asserts the [0,8]
    /// snapshot keeps its own bloom.
    /// </summary>
    [Test]
    public void LargeBoundary_DoesNotShareBloomIntoSnapshotExtendingBelowFrom()
    {
        // CompactSize=1 makes every block a boundary; MaxCompactSize=16 so block 16's window is [0, 16].
        using FlatTestContainer tier = new(
            arenaFileSizeBytes: 256 * 1024,
            blobFileSizeBytes: 4 * 1024 * 1024,
            configure: b => b.AddSingleton<ICompactionSchedule>(
                ScheduleHelper.CreateWithOffset(new FlatDbConfig { CompactSize = 1, PersistedSnapshotMaxCompactSize = 16 }, 0)));
        SnapshotRepository repo = tier.Repository;
        PersistedSnapshotCompactor compactor = tier.Compactor;

        StateId[] states = new StateId[17];
        states[0] = new StateId(0, Keccak.EmptyTreeHash);
        for (int i = 1; i <= 16; i++)
            states[i] = new StateId((ulong)i, Keccak.Compute($"{i}"));

        // Build base [0..8], then the [0,8] large-compacted skip-pointer.
        for (int i = 1; i <= 8; i++)
            BuildBase(tier, states, i);
        compactor.DoCompactSnapshot(states[8], persistedBlockNumber: 0);

        // Build base [9..16], then the [0,16] window clamped to persistence point 4 → big merge is [4,16].
        for (int i = 9; i <= 16; i++)
            BuildBase(tier, states, i);
        compactor.DoCompactSnapshot(states[16], persistedBlockNumber: 4);

        Assert.That(repo.TryLeasePersistedState(states[16], SnapshotTier.PersistedLargeCompacted, out PersistedSnapshot? big), Is.True);
        using (big)
        {
            Assert.That(big!.From.BlockNumber, Is.EqualTo(4), "precondition: the big merge is clamped to From=4");
            Assert.That(repo.TryLeasePersistedState(states[8], SnapshotTier.PersistedLargeCompacted, out PersistedSnapshot? below), Is.True);
            using (below)
                Assert.That(ReferenceEquals(below!.Bloom, big.Bloom), Is.False,
                    "a [0,8] snapshot extending below from=4 must keep its own bloom");
        }
    }

    /// <summary>
    /// Sizing the merged bloom must count a filter shared by several sources only once. A large
    /// compaction adopts its (superset) bloom across the snapshots it contains, so a later compaction
    /// can assemble several sources that all point at that one filter — each reporting its whole-window
    /// key count. Summing per source inflates <c>bloomCapacity</c> (and thus the merged filter) by the
    /// number of sharers. Builds a [0,8] large skip-pointer that shares its bloom across bases [1,8],
    /// then a [4,16] merge clamped to persistence 4 assembling bases [5,16] — bases [5,8] share the
    /// [0,8] bloom — and asserts the merged filter's capacity equals the deduplicated source-bloom sum,
    /// not the inflated per-source sum.
    /// </summary>
    [Test]
    public void LargeBoundary_MergedBloomCapacity_DeduplicatesSharedSourceBloom()
    {
        // CompactSize=1 makes every block a boundary; MaxCompactSize=16 so block 16's window is [0, 16].
        using FlatTestContainer tier = new(
            arenaFileSizeBytes: 256 * 1024,
            blobFileSizeBytes: 4 * 1024 * 1024,
            configure: b => b.AddSingleton<ICompactionSchedule>(
                ScheduleHelper.CreateWithOffset(new FlatDbConfig { CompactSize = 1, PersistedSnapshotMaxCompactSize = 16 }, 0)));
        SnapshotRepository repo = tier.Repository;
        PersistedSnapshotCompactor compactor = tier.Compactor;

        StateId[] states = new StateId[17];
        states[0] = new StateId(0, Keccak.EmptyTreeHash);
        for (int i = 1; i <= 16; i++)
            states[i] = new StateId((ulong)i, Keccak.Compute($"{i}"));

        // Build base [0..8], then the [0,8] large-compacted skip-pointer — it shares its bloom over [1,8].
        for (int i = 1; i <= 8; i++)
            BuildBase(tier, states, i);
        compactor.DoCompactSnapshot(states[8], persistedBlockNumber: 0);

        // Build base [9..16]; the [0,16] window clamps to persistence 4, so the merge spans [4,16] and
        // assembles bases [5,16] — bases [5,8] still carry the shared [0,8] bloom.
        for (int i = 9; i <= 16; i++)
            BuildBase(tier, states, i);

        // Capture the source blooms the merge will see, BEFORE it runs and replaces them with its own
        // shared bloom. dedupedSum counts each distinct filter once (the [0,8] bloom across bases [5,8]);
        // naiveSum is the buggy per-source sum that double-counts it.
        long dedupedSum = 0, naiveSum = 0;
        HashSet<BloomFilter> distinct = [];
        for (int i = 5; i <= 16; i++)
        {
            Assert.That(repo.TryLeasePersistedState(states[i], SnapshotTier.PersistedBase, out PersistedSnapshot? src), Is.True);
            using (src)
            {
                naiveSum += src!.Bloom.Count;
                if (distinct.Add(src.Bloom)) dedupedSum += src.Bloom.Count;
            }
        }
        Assert.That(distinct.Count, Is.LessThan(12), "precondition: bases [5,8] must share one bloom, so fewer than 12 distinct filters");
        Assert.That(dedupedSum, Is.LessThan(naiveSum), "precondition: the shared bloom is double-counted by a naive per-source sum");

        compactor.DoCompactSnapshot(states[16], persistedBlockNumber: 4);

        Assert.That(repo.TryLeasePersistedState(states[16], SnapshotTier.PersistedLargeCompacted, out PersistedSnapshot? big), Is.True);
        using (big)
        {
            Assert.That(big!.From.BlockNumber, Is.EqualTo(4), "precondition: the merge is clamped to From=4");
            Assert.That(big.Bloom.Capacity, Is.EqualTo(dedupedSum),
                "merged bloom capacity must count the shared source bloom once, not once per sharer");
        }
    }
}
