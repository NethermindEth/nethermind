// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.Db;
using Nethermind.State.Flat.BlockRangeTrieForest;
using Nethermind.State.Flat.PersistedSnapshots;
using Nethermind.State.Flat.Storage;
using Nethermind.Trie;
using NUnit.Framework;
using ForestImpl = Nethermind.State.Flat.BlockRangeTrieForest.BlockRangeTrieForest;

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

    private PersistedSnapshot CreatePersistedSnapshot(int id, StateId from, StateId to, PersistedSnapshotType type, byte[] data)
    {
        using ArenaWriter writer = _memArena.CreateWriter(data.Length);
        Span<byte> span = writer.GetWriter().GetSpan(data.Length);
        data.CopyTo(span);
        writer.GetWriter().Advance(data.Length);
        (_, ArenaReservation reservation) = writer.Complete();
        return new PersistedSnapshot(id, from, to, type, reservation);
    }

    [Test]
    public void TryCompactPersistedSnapshots_MergesMultipleBaseSnapshots()
    {
        string testDir = Path.Combine(Path.GetTempPath(), $"nethermind_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(testDir);
        try
        {
            using ArenaManager baseArena = new(Path.Combine(testDir, "arenas", "base"), maxArenaSize: 64 * 1024);
            using ArenaManager compactedArena = new(Path.Combine(testDir, "arenas", "compacted"), maxArenaSize: 64 * 1024);
            using PersistedSnapshotRepository repo = new(baseArena, compactedArena, testDir, new FlatDbConfig(), NullBlockRangeTrieForest.Instance);
            repo.LoadFromCatalog();

            // CompactSize=4, MinCompactSize=2. Use 8 blocks so compactSize = 8 & -8 = 8 > CompactSize=4, triggering compaction.
            // (compactSize == _compactSize is now skipped since persistable snapshots are produced by PersistenceManager)
            IFlatDbConfig config = new FlatDbConfig { CompactSize = 4, MinCompactSize = 2 };
            PersistedSnapshotCompactor compactor = new(repo, compactedArena, config, Nethermind.Logging.LimboLogs.Instance);

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
            Assert.That(compacted.TryGetAccount(TestItem.AddressA, out _), Is.True);
            Assert.That(compacted.TryGetAccount(TestItem.AddressB, out _), Is.True);
            Assert.That(compacted.TryGetAccount(TestItem.AddressC, out _), Is.True);
            Assert.That(compacted.TryGetAccount(TestItem.AddressD, out _), Is.True);
            Assert.That(compacted.TryGetAccount(TestItem.AddressE, out _), Is.True);
            Assert.That(compacted.TryGetAccount(TestItem.AddressF, out _), Is.True);
            Assert.That(compacted.TryGetAccount(TestItem.Addresses[6], out _), Is.True);
            Assert.That(compacted.TryGetAccount(TestItem.Addresses[7], out _), Is.True);
            compacted.Dispose();
        }
        finally
        {
            if (Directory.Exists(testDir))
                Directory.Delete(testDir, recursive: true);
        }
    }

    [Test]
    public void CompactedSnapshot_Merge_ProducesValidHsst()
    {
        StateId s0 = new(0, Keccak.EmptyTreeHash);
        StateId s1 = new(1, Keccak.Compute("1"));
        StateId s2 = new(2, Keccak.Compute("2"));

        TreePath path = new(Keccak.Compute("path"), 4);

        SnapshotContent content1 = new();
        content1.StateNodes[path] = new TrieNode(NodeType.Leaf, [0xC0]);
        Snapshot snap1 = new(s0, s1, content1, _pool, ResourcePool.Usage.MainBlockProcessing);
        byte[] data1 = PersistedSnapshotBuilderTestExtensions.Build(snap1);

        SnapshotContent content2 = new();
        content2.StateNodes[path] = new TrieNode(NodeType.Leaf, [0xC1, 0x80]);
        Snapshot snap2 = new(s1, s2, content2, _pool, ResourcePool.Usage.MainBlockProcessing);
        byte[] data2 = PersistedSnapshotBuilderTestExtensions.Build(snap2);

        PersistedSnapshot baseSnap0 = CreatePersistedSnapshot(0, s0, s1, PersistedSnapshotType.Full, data1);
        PersistedSnapshot baseSnap1 = CreatePersistedSnapshot(1, s1, s2, PersistedSnapshotType.Full, data2);
        PersistedSnapshotList toMerge = new(2);
        toMerge.Add(baseSnap0);
        toMerge.Add(baseSnap1);
        byte[] merged = PersistedSnapshotBuilderTestExtensions.MergeSnapshots(toMerge);

        // Read merged bytes directly to verify metadata exists
        Hsst.Hsst outer = new(merged);
        Assert.That(outer.TryGet(PersistedSnapshot.MetadataTag, out ReadOnlySpan<byte> metaColumn), Is.True);
        Assert.That(metaColumn.Length, Is.GreaterThan(0));
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
            byte[] data = PersistedSnapshotBuilderTestExtensions.Build(snap);
            toMerge.Add(CreatePersistedSnapshot(i, prevState, nextState, PersistedSnapshotType.Full, data));
            prevState = nextState;
        }

        byte[] merged = PersistedSnapshotBuilderTestExtensions.MergeSnapshots(toMerge);
        PersistedSnapshot compacted = CreatePersistedSnapshot(100, toMerge[0].From, toMerge[toMerge.Count - 1].To,
            PersistedSnapshotType.Linked, merged);
        PersistedSnapshotUtils.ValidateCompactedPersistedSnapshot(compacted, toMerge, true);
    }

    [Test]
    public void ForestSpilledSnapshot_IsForestSpilledTrue_NodeRlpReturnsFalse_AccountsRoundTrip()
    {
        StateId s0 = new(0, Keccak.EmptyTreeHash);
        StateId s1 = new(1, Keccak.Compute("1"));
        StateId s2 = new(2, Keccak.Compute("2"));

        TreePath path = new(Keccak.Compute("path"), 4);
        byte[] rlp1 = Bytes.FromHexString("C080");

        SnapshotContent content1 = new();
        content1.StateNodes[path] = new TrieNode(NodeType.Leaf, rlp1);
        content1.Accounts[TestItem.AddressA] = Build.An.Account.WithBalance(100).TestObject;
        Snapshot snap1 = new(s0, s1, content1, _pool, ResourcePool.Usage.MainBlockProcessing);
        byte[] data1 = PersistedSnapshotBuilderTestExtensions.Build(snap1);

        SnapshotContent content2 = new();
        content2.Accounts[TestItem.AddressB] = Build.An.Account.WithBalance(200).TestObject;
        Snapshot snap2 = new(s1, s2, content2, _pool, ResourcePool.Usage.MainBlockProcessing);
        byte[] data2 = PersistedSnapshotBuilderTestExtensions.Build(snap2);

        PersistedSnapshot base1 = CreatePersistedSnapshot(0, s0, s1, PersistedSnapshotType.Full, data1);
        PersistedSnapshot base2 = CreatePersistedSnapshot(1, s1, s2, PersistedSnapshotType.Full, data2);
        PersistedSnapshotList toMerge = new(2);
        toMerge.Add(base1);
        toMerge.Add(base2);

        byte[] noTrieData = PersistedSnapshotBuilderTestExtensions.MergeSnapshotsNoTrie(toMerge);
        PersistedSnapshot spilled = CreatePersistedSnapshot(2, s0, s2, PersistedSnapshotType.Linked, noTrieData);

        Assert.That(spilled.IsForestSpilled, Is.True);
        Assert.That(spilled.TryLoadStateNodeHash(path, out ValueHash256 _), Is.False);
        Assert.That(spilled.TryGetAccount(TestItem.AddressA, out ReadOnlySpan<byte> _), Is.True);
        Assert.That(spilled.TryGetAccount(TestItem.AddressB, out ReadOnlySpan<byte> _), Is.True);
    }

    [Test]
    public void Compactor_WithForest_DumpsFullSnapshotTrieNodes_OutputIsForestSpilled()
    {
        string testDir = Path.Combine(Path.GetTempPath(), $"nethermind_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(testDir);
        try
        {
            using ArenaManager baseArena = new(Path.Combine(testDir, "arenas", "base"), maxArenaSize: 64 * 1024);
            using ArenaManager compactedArena = new(Path.Combine(testDir, "arenas", "compacted"), maxArenaSize: 64 * 1024);
            // CompactSize=4, MinCompactSize=2, BlockRangePerForest=4
            // At block 8: compactSize = 8 & -8 = 8 > CompactSize=4 → triggers forest-spilled compaction
            IFlatDbConfig config = new FlatDbConfig { CompactSize = 4, MinCompactSize = 2, BlockRangePerForest = 4 };
            using SnapshotableMemDb forestDb = new();
            ForestImpl forest = new(forestDb);
            using PersistedSnapshotRepository repo = new(baseArena, compactedArena, testDir, config, forest);
            repo.LoadFromCatalog();

            PersistedSnapshotCompactor compactor = new(repo, compactedArena, config, Nethermind.Logging.LimboLogs.Instance);

            StateId s0 = new(0, Keccak.EmptyTreeHash);
            TreePath path = new(Keccak.Compute("statePath"), 4);
            byte[] nodeRlp = Bytes.FromHexString("C080");

            // Create 8 consecutive base snapshots; put a trie node in the first one
            StateId prev = s0;
            for (int i = 1; i <= 8; i++)
            {
                StateId next = new(i, Keccak.Compute(i.ToString()));
                SnapshotContent content = new();
                if (i == 1)
                {
                    content.StateNodes[path] = new TrieNode(NodeType.Leaf, nodeRlp);
                }
                content.Accounts[TestItem.Addresses[i - 1]] = Build.An.Account.WithBalance((UInt256)i * 100).TestObject;
                repo.ConvertSnapshotToPersistedSnapshot(new Snapshot(prev, next, content, _pool, ResourcePool.Usage.MainBlockProcessing));
                prev = next;
            }

            StateId s8 = new(8, Keccak.Compute("8"));
            compactor.DoCompactSnapshot(s8);

            // Compacted snapshot at block 8 should be forest-spilled
            Assert.That(repo.TryLeaseCompactedSnapshotTo(s8, out PersistedSnapshot? compacted), Is.True);
            Assert.That(compacted!.IsForestSpilled, Is.True);
            Assert.That(compacted.TryLoadStateNodeHash(path, out ValueHash256 _), Is.False);

            // All accounts are still readable
            for (int i = 0; i < 8; i++)
                Assert.That(compacted.TryGetAccount(TestItem.Addresses[i], out ReadOnlySpan<byte> _), Is.True);

            // The forest should contain the trie node from block 1 (range 0 = floor(1/4) = 0)
            Hash256 hash = Keccak.Compute(nodeRlp);
            byte[]? rlpFromForest = forest.TryGetState(0, path, hash);
            Assert.That(rlpFromForest, Is.EqualTo(nodeRlp));

            compacted.Dispose();
        }
        finally
        {
            if (Directory.Exists(testDir))
                Directory.Delete(testDir, recursive: true);
        }
    }

    [Test]
    public void BaseSnapshot_Build_ProducesValidHsst()
    {
        StateId s0 = new(0, Keccak.EmptyTreeHash);
        StateId s1 = new(1, Keccak.Compute("1"));

        SnapshotContent content = new();
        content.StateNodes[new TreePath(Keccak.Compute("path"), 4)] = new TrieNode(NodeType.Leaf, [0xC0]);
        Snapshot snap = new(s0, s1, content, _pool, ResourcePool.Usage.MainBlockProcessing);
        byte[] data = PersistedSnapshotBuilderTestExtensions.Build(snap);

        PersistedSnapshot persisted = CreatePersistedSnapshot(0, s0, s1, PersistedSnapshotType.Full, data);
        Assert.That(persisted.IsForestSpilled, Is.False);
    }

    [Test]
    public void BaseSnapshot_TryLoadStateNodeHash_ReturnsCorrectHash()
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
        byte[] data1 = PersistedSnapshotBuilderTestExtensions.Build(snap1);

        SnapshotContent content2 = new();
        content2.StateNodes[path2] = new TrieNode(NodeType.Leaf, rlp2);
        Snapshot snap2 = new(s1, s2, content2, _pool, ResourcePool.Usage.MainBlockProcessing);
        byte[] data2 = PersistedSnapshotBuilderTestExtensions.Build(snap2);

        PersistedSnapshot baseSnap0 = CreatePersistedSnapshot(0, s0, s1, PersistedSnapshotType.Full, data1);
        PersistedSnapshot baseSnap1 = CreatePersistedSnapshot(1, s1, s2, PersistedSnapshotType.Full, data2);

        Assert.That(baseSnap0.TryLoadStateNodeHash(path1, out ValueHash256 hash1), Is.True);
        Assert.That(hash1, Is.EqualTo(ValueKeccak.Compute(rlp1)));

        Assert.That(baseSnap1.TryLoadStateNodeHash(path2, out ValueHash256 hash2), Is.True);
        Assert.That(hash2, Is.EqualTo(ValueKeccak.Compute(rlp2)));
    }
}
