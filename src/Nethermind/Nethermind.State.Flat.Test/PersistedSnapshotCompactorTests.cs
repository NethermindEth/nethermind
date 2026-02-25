// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.Db;
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
        SnapshotLocation loc = _memArena.Allocate(data);
        ArenaReservation reservation = _memArena.Open(loc);
        return new PersistedSnapshot(id, from, to, type, reservation, referencedSnapshots);
    }

    [Test]
    public void TryCompactPersistedSnapshots_MergesMultipleBaseSnapshots()
    {
        string testDir = Path.Combine(Path.GetTempPath(), $"nethermind_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(testDir);
        try
        {
            using ArenaManager arenaM = new(Path.Combine(testDir, "arenas"), maxArenaSize: 64 * 1024);
            using PersistedSnapshotRepository repo = new(arenaM, testDir, new FlatDbConfig());
            repo.LoadFromCatalog();

            // CompactSize=4, MinCompactSize=2 so compaction triggers at block 4
            IFlatDbConfig config = new FlatDbConfig { CompactSize = 4, MinCompactSize = 2 };
            PersistedSnapshotCompactor compactor = new(repo, arenaM, config, Nethermind.Logging.LimboLogs.Instance);

            StateId s0 = new(0, Keccak.EmptyTreeHash);
            StateId s1 = new(1, Keccak.Compute("1"));
            StateId s2 = new(2, Keccak.Compute("2"));
            StateId s3 = new(3, Keccak.Compute("3"));
            StateId s4 = new(4, Keccak.Compute("4"));

            // Create 4 consecutive base snapshots with different accounts
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

            compactor.DoCompactSnapshot(s4);

            // Compaction should have been triggered at block 4 (4 & -4 == 4 >= MinCompactSize=2)
            // Verify compacted snapshot exists and contains all data
            Assert.That(repo.TryLeaseCompactedSnapshotTo(s4, out PersistedSnapshot? compacted), Is.True);
            Assert.That(compacted!.From, Is.EqualTo(s0));
            Assert.That(compacted.TryGetAccount(TestItem.AddressA, out _), Is.True);
            Assert.That(compacted.TryGetAccount(TestItem.AddressB, out _), Is.True);
            Assert.That(compacted.TryGetAccount(TestItem.AddressC, out _), Is.True);
            Assert.That(compacted.TryGetAccount(TestItem.AddressD, out _), Is.True);
            compacted.Dispose();
        }
        finally
        {
            if (Directory.Exists(testDir))
                Directory.Delete(testDir, recursive: true);
        }
    }

    [Test]
    public void CompactedSnapshot_HasNodeRefsAndRefIds_InMetadata()
    {
        StateId s0 = new(0, Keccak.EmptyTreeHash);
        StateId s1 = new(1, Keccak.Compute("1"));
        StateId s2 = new(2, Keccak.Compute("2"));

        TreePath path = new(Keccak.Compute("path"), 4);

        SnapshotContent content1 = new();
        content1.StateNodes[path] = new TrieNode(NodeType.Leaf, [0xC0]);
        Snapshot snap1 = new(s0, s1, content1, _pool, ResourcePool.Usage.MainBlockProcessing);
        byte[] data1 = PersistedSnapshotBuilder.Build(snap1);

        SnapshotContent content2 = new();
        content2.StateNodes[path] = new TrieNode(NodeType.Leaf, [0xC1, 0x80]);
        Snapshot snap2 = new(s1, s2, content2, _pool, ResourcePool.Usage.MainBlockProcessing);
        byte[] data2 = PersistedSnapshotBuilder.Build(snap2);

        PersistedSnapshot baseSnap0 = CreatePersistedSnapshot(0, s0, s1, PersistedSnapshotType.Full, data1);
        PersistedSnapshot baseSnap1 = CreatePersistedSnapshot(1, s1, s2, PersistedSnapshotType.Full, data2);
        PersistedSnapshotList toMerge = new(2);
        toMerge.Add(baseSnap0);
        toMerge.Add(baseSnap1);
        byte[] merged = PersistedSnapshotBuilder.MergeSnapshots(toMerge);

        // Read merged bytes directly to verify metadata
        Hsst.Hsst outer = new(merged);
        Assert.That(outer.TryGet(PersistedSnapshot.MetadataTag, out ReadOnlySpan<byte> metaColumn), Is.True);
        Hsst.Hsst meta = new(metaColumn);

        // "noderefs" key with value [0x01]
        Assert.That(meta.TryGet("noderefs"u8, out ReadOnlySpan<byte> nodeRefsValue), Is.True);
        Assert.That(nodeRefsValue.ToArray(), Is.EqualTo(new byte[] { 0x01 }));

        // "ref_ids" key with both base snapshot IDs as LE int32s
        Assert.That(meta.TryGet("ref_ids"u8, out ReadOnlySpan<byte> refIdsValue), Is.True);
        Assert.That(refIdsValue.Length, Is.EqualTo(8)); // 2 IDs × 4 bytes

        // ReadRefIdsFromMetadata should return both IDs
        int[]? refIds = PersistedSnapshot.ReadRefIdsFromMetadata(merged);
        Assert.That(refIds, Is.Not.Null);
        Assert.That(refIds, Does.Contain(0));
        Assert.That(refIds, Does.Contain(1));
    }

    private static IEnumerable<TestCaseData> MergeValidationTestCases()
    {
        // Basic: two snapshots with overlapping accounts
        {
            SnapshotContent c0 = new();
            c0.Accounts[TestItem.AddressA] = Build.An.Account.WithBalance(100).TestObject;
            SnapshotContent c1 = new();
            c1.Accounts[TestItem.AddressA] = Build.An.Account.WithBalance(200).TestObject;
            yield return new TestCaseData((object)new[] { c0, c1 }).SetName("Merge_AccountOverride");
        }

        // Regression: advance-corrupts-minKey bug in NWayStreamingMerge (StateTopNodes).
        // snapshot[0] has paths {A, B}, snapshot[1] has only {B} with different RLP.
        {
            TreePath pathA = new(Hash256.Zero, 4);
            TreePath pathB = new(new Hash256("0x1000000000000000000000000000000000000000000000000000000000000000"), 4);
            SnapshotContent c0 = new();
            c0.StateNodes[pathA] = new TrieNode(NodeType.Leaf, [0xC0, 0x80]);
            c0.StateNodes[pathB] = new TrieNode(NodeType.Leaf, [0xC0, 0x80]);
            SnapshotContent c1 = new();
            c1.StateNodes[pathB] = new TrieNode(NodeType.Leaf, [0xC1, 0x80]);
            yield return new TestCaseData((object)new[] { c0, c1 }).SetName("Merge_AdvanceOrder_StateTopNodes");
        }

        // Regression: same bug in NWayInnerMerge (StorageNodes inner merge).
        // snapshot[0] has storage trie nodes for an address at {pathA, pathB},
        // snapshot[1] has only {pathB} with different RLP.
        {
            Hash256 storageAddr = Keccak.Compute("storageAddr");
            TreePath pathA = new(Hash256.Zero, 8);
            TreePath pathB = new(new Hash256("0x1000000000000000000000000000000000000000000000000000000000000000"), 8);
            SnapshotContent c0 = new();
            c0.StorageNodes[(storageAddr, pathA)] = new TrieNode(NodeType.Leaf, [0xC1, 0x80]);
            c0.StorageNodes[(storageAddr, pathB)] = new TrieNode(NodeType.Leaf, [0xC1, 0x80]);
            SnapshotContent c1 = new();
            c1.StorageNodes[(storageAddr, pathB)] = new TrieNode(NodeType.Leaf, [0xC2, 0x80, 0x81]);
            yield return new TestCaseData((object)new[] { c0, c1 }).SetName("Merge_AdvanceOrder_StorageNodes");
        }

        // Mixed: all data types across two snapshots
        {
            Hash256 storageAddr = Keccak.Compute("storageAddr");
            TreePath statePath = new(Keccak.Compute("statePath"), 4);
            SnapshotContent c0 = new();
            c0.Accounts[TestItem.AddressA] = Build.An.Account.WithBalance(100).TestObject;
            c0.Storages[(TestItem.AddressA, 1)] = new SlotValue(new byte[] { 0x42 });
            c0.SelfDestructedStorageAddresses[TestItem.AddressB] = true;
            c0.StateNodes[statePath] = new TrieNode(NodeType.Leaf, [0xC0, 0x80]);
            c0.StorageNodes[(storageAddr, new TreePath(Hash256.Zero, 4))] = new TrieNode(NodeType.Leaf, [0xC1, 0x80]);
            SnapshotContent c1 = new();
            c1.Accounts[TestItem.AddressA] = Build.An.Account.WithBalance((UInt256)200).TestObject;
            c1.Storages[(TestItem.AddressA, 2)] = new SlotValue(new byte[] { 0x99 });
            c1.StateNodes[statePath] = new TrieNode(NodeType.Leaf, [0xC1, 0x80]);
            c1.StorageNodes[(storageAddr, new TreePath(Hash256.Zero, 4))] = new TrieNode(NodeType.Leaf, [0xC2, 0x80, 0x81]);
            yield return new TestCaseData((object)new[] { c0, c1 }).SetName("Merge_MixedDataTypes");
        }

        // Overlapping state node (newer wins) + non-overlapping accounts (both preserved)
        {
            TreePath path = new(Keccak.Compute("path"), 4);
            SnapshotContent c0 = new();
            c0.StateNodes[path] = new TrieNode(NodeType.Leaf, [0xC0]);
            c0.Accounts[TestItem.AddressA] = Build.An.Account.WithBalance(100).TestObject;
            SnapshotContent c1 = new();
            c1.StateNodes[path] = new TrieNode(NodeType.Leaf, [0xC1, 0x80]);
            c1.Accounts[TestItem.AddressB] = Build.An.Account.WithBalance(200).TestObject;
            yield return new TestCaseData((object)new[] { c0, c1 }).SetName("Merge_NewerOverridesOlder");
        }

        // Two distinct state node paths, both survive merge
        {
            SnapshotContent c0 = new();
            c0.StateNodes[new TreePath(Keccak.Compute("path1"), 4)] = new TrieNode(NodeType.Leaf, [0xC0]);
            SnapshotContent c1 = new();
            c1.StateNodes[new TreePath(Keccak.Compute("path2"), 4)] = new TrieNode(NodeType.Leaf, [0xC1, 0x80]);
            yield return new TestCaseData((object)new[] { c0, c1 }).SetName("Merge_PreservesNonOverlapping");
        }

        // Older slot cleared by self-destruct, newer slot + flag preserved
        {
            SnapshotContent c0 = new();
            c0.Storages[(TestItem.AddressA, 1)] = new SlotValue(new byte[] { 0x42 });
            SnapshotContent c1 = new();
            c1.SelfDestructedStorageAddresses[TestItem.AddressA] = false;
            c1.Storages[(TestItem.AddressA, 2)] = new SlotValue(new byte[] { 0x99 });
            yield return new TestCaseData((object)new[] { c0, c1 }).SetName("Merge_SelfDestruct_ClearsOlderStorage");
        }

        // Newer true flag doesn't overwrite older false (destructed) — TryAdd semantics
        {
            SnapshotContent c0 = new();
            c0.SelfDestructedStorageAddresses[TestItem.AddressA] = false;
            SnapshotContent c1 = new();
            c1.SelfDestructedStorageAddresses[TestItem.AddressA] = true;
            yield return new TestCaseData((object)new[] { c0, c1 }).SetName("Merge_SelfDestruct_TryAddSemantics");
        }

        // Storage trie nodes survive self-destruct
        {
            Hash256 addrHash = Keccak.Compute(TestItem.AddressA.Bytes);
            TreePath storagePath = new(Keccak.Compute("storage_path"), 4);
            SnapshotContent c0 = new();
            c0.StorageNodes[(addrHash, storagePath)] = new TrieNode(NodeType.Leaf, [0xC1, 0x80]);
            SnapshotContent c1 = new();
            c1.SelfDestructedStorageAddresses[TestItem.AddressA] = false;
            yield return new TestCaseData((object)new[] { c0, c1 }).SetName("Merge_SelfDestruct_StorageNodesKept");
        }
    }

    [TestCaseSource(nameof(MergeValidationTestCases))]
    public void MergeSnapshots_ValidatesCorrectly(SnapshotContent[] contents)
    {
        PersistedSnapshotList toMerge = new(contents.Length);
        StateId prevState = new(0, Keccak.EmptyTreeHash);

        for (int i = 0; i < contents.Length; i++)
        {
            StateId nextState = new(i + 1, Keccak.Compute($"{i + 1}"));
            Snapshot snap = new(prevState, nextState, contents[i], _pool, ResourcePool.Usage.MainBlockProcessing);
            byte[] data = PersistedSnapshotBuilder.Build(snap);
            toMerge.Add(CreatePersistedSnapshot(i, prevState, nextState, PersistedSnapshotType.Full, data));
            prevState = nextState;
        }

        byte[] merged = PersistedSnapshotBuilder.MergeSnapshots(toMerge);
        PersistedSnapshot compacted = CreatePersistedSnapshot(100, toMerge[0].From, toMerge[toMerge.Count - 1].To,
            PersistedSnapshotType.Linked, merged);
        PersistedSnapshotUtils.ValidateCompactedPersistedSnapshot(compacted, toMerge, true);
    }

    [Test]
    public void ReadRefIdsFromMetadata_ReturnsNull_ForBaseSnapshot()
    {
        StateId s0 = new(0, Keccak.EmptyTreeHash);
        StateId s1 = new(1, Keccak.Compute("1"));

        SnapshotContent content = new();
        content.StateNodes[new TreePath(Keccak.Compute("path"), 4)] = new TrieNode(NodeType.Leaf, [0xC0]);
        Snapshot snap = new(s0, s1, content, _pool, ResourcePool.Usage.MainBlockProcessing);
        byte[] data = PersistedSnapshotBuilder.Build(snap);

        int[]? refIds = PersistedSnapshot.ReadRefIdsFromMetadata(data);
        Assert.That(refIds, Is.Null);
    }

    [Test]
    public void CompactedSnapshot_NodeRefResolution_WorksWithMetadataFlag()
    {
        StateId s0 = new(0, Keccak.EmptyTreeHash);
        StateId s1 = new(1, Keccak.Compute("1"));
        StateId s2 = new(2, Keccak.Compute("2"));

        TreePath path1 = new(Keccak.Compute("path1"), 4);
        TreePath path2 = new(Keccak.Compute("path2"), 4);
        byte[] rlp1 = [0xC0];
        byte[] rlp2 = [0xC1, 0x80];

        SnapshotContent content1 = new();
        content1.StateNodes[path1] = new TrieNode(NodeType.Leaf, rlp1);
        Snapshot snap1 = new(s0, s1, content1, _pool, ResourcePool.Usage.MainBlockProcessing);
        byte[] data1 = PersistedSnapshotBuilder.Build(snap1);

        SnapshotContent content2 = new();
        content2.StateNodes[path2] = new TrieNode(NodeType.Leaf, rlp2);
        Snapshot snap2 = new(s1, s2, content2, _pool, ResourcePool.Usage.MainBlockProcessing);
        byte[] data2 = PersistedSnapshotBuilder.Build(snap2);

        PersistedSnapshot baseSnap0 = CreatePersistedSnapshot(0, s0, s1, PersistedSnapshotType.Full, data1);
        PersistedSnapshot baseSnap1 = CreatePersistedSnapshot(1, s1, s2, PersistedSnapshotType.Full, data2);
        PersistedSnapshotList toMerge = new(2);
        toMerge.Add(baseSnap0);
        toMerge.Add(baseSnap1);
        byte[] merged = PersistedSnapshotBuilder.MergeSnapshots(toMerge);

        // With referenced snapshots: NodeRefs resolve to actual RLP
        PersistedSnapshot compactedWithRefs = CreatePersistedSnapshot(2, s0, s2, PersistedSnapshotType.Linked, merged,
            [baseSnap0, baseSnap1]);
        Assert.That(compactedWithRefs.TryLoadStateNodeRlp(path1, out ReadOnlySpan<byte> resolved1), Is.True);
        Assert.That(resolved1.ToArray(), Is.EqualTo(rlp1));
        Assert.That(compactedWithRefs.TryLoadStateNodeRlp(path2, out ReadOnlySpan<byte> resolved2), Is.True);
        Assert.That(resolved2.ToArray(), Is.EqualTo(rlp2));

        // Without referenced snapshots: returns raw NodeRef bytes (8 bytes)
        PersistedSnapshot compactedWithoutRefs = CreatePersistedSnapshot(3, s0, s2, PersistedSnapshotType.Linked, merged);
        Assert.That(compactedWithoutRefs.TryLoadStateNodeRlp(path1, out ReadOnlySpan<byte> raw1), Is.True);
        Assert.That(raw1.Length, Is.EqualTo(NodeRef.Size));
        Assert.That(compactedWithoutRefs.TryLoadStateNodeRlp(path2, out ReadOnlySpan<byte> raw2), Is.True);
        Assert.That(raw2.Length, Is.EqualTo(NodeRef.Size));
    }
}
